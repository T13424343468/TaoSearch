using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex;
using TaoSearch.Kernel.Store;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测（测试规范九.11）：JournalWriter 重放坏帧全路径、JournalSink 生命周期、
/// IndexPersistence 断点 Replace/恢复注入引擎/坏日志兜底。
/// </summary>
public sealed class StoreBranchTests
{
    private static byte[] Header() => "TSSJ"u8.ToArray().Concat(new byte[] { 1, 0, 0, 0 }).ToArray();

    private static byte[] Frame(byte op, byte[] payload, bool corruptCrc = false)
    {
        var ms = new MemoryStream();
        ms.WriteByte(op);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);
        ms.Write(len);
        ms.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(payload) ^ (corruptCrc ? 0xFFFFFFFFu : 0));
        ms.Write(crc);
        return ms.ToArray();
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- JournalWriter.Replay：坏头/坏版本/坏帧/撕裂尾/非可seek流 ----

    [Fact]
    public void Replay_EmptyStream_CleanZeroFrames()
    {
        var r = JournalWriter.Replay(new MemoryStream(), _ => Assert.Fail("不应有事件"));
        Assert.Equal(0, r.FramesApplied);
        Assert.True(r.CleanEnd);
        Assert.Equal(0, r.TruncatedTailBytes);
    }

    [Fact]
    public void Replay_BadMagic_Throws()
    {
        var bytes = "XXXX"u8.ToArray().Concat(new byte[] { 1, 0, 0, 0 }).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            JournalWriter.Replay(new MemoryStream(bytes), _ => { }));
    }

    [Fact]
    public void Replay_BadVersion_Throws()
    {
        var bytes = "TSSJ"u8.ToArray().Concat(new byte[] { 9, 0, 0, 0 }).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            JournalWriter.Replay(new MemoryStream(bytes), _ => { }));
    }

    [Fact]
    public void Replay_BadOp_StopsUnclean()
    {
        var bytes = Header().Concat(new byte[] { 7 }).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
        Assert.Equal(0, r.FramesApplied);
    }

    [Fact]
    public void Replay_TruncatedLenField_StopsUnclean()
    {
        var bytes = Header().Concat(new byte[] { 1, 8, 0 }).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_NegativeLength_StopsUnclean()
    {
        var bytes = Header().Concat(Frame(1, new byte[8])[..9]).ToArray();
        bytes = bytes.Concat(BitConverter.GetBytes(-1)).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_OversizeLength_StopsUnclean()
    {
        var bytes = Header().Concat(Frame(1, new byte[8])[..9]).ToArray();
        bytes = bytes.Concat(BitConverter.GetBytes(1 << 20 | 1)).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_TruncatedPayload_StopsUnclean()
    {
        var bytes = Header().Concat(Frame(1, new byte[8])[..^6]).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_TruncatedCrc_StopsUnclean()
    {
        var bytes = Header().Concat(Frame(1, new byte[8])[..^2]).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_CrcMismatch_StopsUnclean()
    {
        var bytes = Header().Concat(Frame(1, new byte[8], corruptCrc: true)).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_UpsertNameLenNegative_StopsUnclean()
    {
        var payload = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32), -1);
        var bytes = Header().Concat(Frame(1, payload)).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_UpsertNameLenBeyondPayload_StopsUnclean()
    {
        var payload = new byte[36];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(32), 100);
        var bytes = Header().Concat(Frame(1, payload)).ToArray();
        var r = JournalWriter.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void Replay_RemoveEvent_FlagsDelete()
    {
        using var ms = new MemoryStream();
        using (var w = new JournalWriter(ms))
            w.AppendRemove(42);
        ms.Position = 0;
        JournalEvent? seen = null;
        var r = JournalWriter.Replay(ms, e => seen = e);
        Assert.Equal(1, r.FramesApplied);
        Assert.True(r.CleanEnd);
        Assert.True(seen!.Value.IsDelete);
        Assert.Equal(42, seen!.Value.FileReference);
    }

    [Fact]
    public void Replay_NonSeekableStream_TailBytesZero()
    {
        using var ms = new MemoryStream();
        using (var w = new JournalWriter(ms))
            w.AppendUpsert(1, 0, "a.txt", 0, DateTimeOffset.UnixEpoch);
        ms.Position = 0;
        var r = JournalWriter.Replay(new NonSeekableStream(ms), _ => { });
        Assert.Equal(1, r.FramesApplied);
        Assert.Equal(0, r.TruncatedTailBytes);
    }

    [Fact]
    public void Append_AfterDispose_Throws()
    {
        using var ms = new MemoryStream();
        var w = new JournalWriter(ms);
        w.Dispose();
        w.Dispose(); // 幂等
        Assert.Throws<ObjectDisposedException>(() =>
            w.AppendUpsert(1, 0, "x", 0, DateTimeOffset.UnixEpoch));
        Assert.Throws<ObjectDisposedException>(() => w.Flush());
    }

    [Fact]
    public void Append_OversizeName_Throws()
    {
        using var ms = new MemoryStream();
        using var w = new JournalWriter(ms);
        var huge = new string('a', (1 << 20) + 100);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            w.AppendUpsert(1, 0, huge, 0, DateTimeOffset.UnixEpoch));
    }

    // ---- JournalSink：生命周期与轮转 ----

    [Fact]
    public void Sink_WriteAfterDispose_NoOp()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "j.log");
        using var sink = new JournalSink(path);
        sink.Dispose();
        sink.Write(new FileEvent { Kind = FileChangeKind.ContentModified, FileReference = 1, Path = "a", UtcTimestamp = DateTimeOffset.UnixEpoch });
        sink.Dispose(); // 幂等
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Sink_RotateWithoutWriterOrFile_ReturnsZero()
    {
        string dir = NewTempDir();
        using var sink = new JournalSink(Path.Combine(dir, "j.log"));
        Assert.Equal(0, sink.Rotate());
    }

    [Fact]
    public void Sink_RotateWithFile_DiscardsAndDeletes()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "j.log");
        using var sink = new JournalSink(path);
        sink.Write(new FileEvent { Kind = FileChangeKind.Created, FileReference = 1, Path = "a.txt", UtcTimestamp = DateTimeOffset.UnixEpoch });
        Assert.True(File.Exists(path));
        long discarded = sink.Rotate();
        Assert.True(discarded > 0);
        Assert.False(File.Exists(path));
        // 轮转后可继续写入（重开 writer）
        sink.Write(new FileEvent { Kind = FileChangeKind.Created, FileReference = 2, Path = "b.txt", UtcTimestamp = DateTimeOffset.UnixEpoch });
        Assert.True(File.Exists(path));
    }

    // ---- IndexPersistence：Replace 分支、注入引擎恢复、坏日志兜底 ----

    [Fact]
    public void Persistence_SaveCheckpoint_Twice_UsesReplace()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "idx.snap");
        using var engine = new FsIndexEngine();
        using var sink = new JournalSink(Path.Combine(dir, "j.log"));

        var first = IndexPersistence.SaveCheckpoint(engine, snap, sink);
        Assert.Equal(0, first.FileCount);
        Assert.True(File.Exists(snap));

        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = 7, Path = "x.txt", UtcTimestamp = DateTimeOffset.UnixEpoch });
        var second = IndexPersistence.SaveCheckpoint(engine, snap, sink);
        Assert.Equal(1, second.FileCount);
        Assert.True(second.SnapshotBytes > 0);
        Assert.Equal(0, second.JournalDiscardedBytes); // Rotate 已在首次后清空
    }

    [Fact]
    public void Persistence_Restore_WithInjectedEngine_UsesItDirectly()
    {
        string dir = NewTempDir();
        using var engine = new FsIndexEngine();
        engine.ApplyEvent(new FileEvent { Kind = FileChangeKind.Created, FileReference = 9, Path = "y.txt", UtcTimestamp = DateTimeOffset.UnixEpoch });

        var r = IndexPersistence.Restore(
            Path.Combine(dir, "missing.snap"), Path.Combine(dir, "missing.log"), "C:", engine);
        Assert.True(r.SnapshotUsed);
        Assert.Equal(1, r.FileCount);
        Assert.Equal(0, r.JournalFramesApplied);
    }

    [Fact]
    public void Persistence_Restore_NoSnapshotNoJournal_EmptyIndex()
    {
        string dir = NewTempDir();
        var r = IndexPersistence.Restore(Path.Combine(dir, "s.snap"), Path.Combine(dir, "j.log"), "C:");
        Assert.False(r.SnapshotUsed);
        Assert.Equal(0, r.FileCount);
    }

    [Fact]
    public void Persistence_Restore_CorruptJournalHeader_DegradesGracefully()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "s.snap");
        string jrn = Path.Combine(dir, "j.log");
        using (var engine = new FsIndexEngine())
            IndexPersistence.SaveCheckpoint(engine, snap, new JournalSink(jrn));
        File.WriteAllBytes(jrn, "GARBAGE"u8.ToArray());

        var r = IndexPersistence.Restore(snap, jrn, "C:");
        Assert.True(r.SnapshotUsed);
        Assert.Equal(0, r.JournalFramesApplied);
    }

    [Fact]
    public void Persistence_Restore_SnapshotPlusJournal_ReplaysEvents()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "s.snap");
        string jrn = Path.Combine(dir, "j.log");
        using (var engine = new FsIndexEngine())
            IndexPersistence.SaveCheckpoint(engine, snap, new JournalSink(jrn));

        using (var w = JournalWriter.OpenAppend(jrn))
        {
            w.AppendUpsert(11, 0, "z.txt", 0, DateTimeOffset.UnixEpoch);
            w.AppendRemove(11);
            w.AppendUpsert(12, 0, "w.txt", 0, DateTimeOffset.UnixEpoch);
        }

        var r = IndexPersistence.Restore(snap, jrn, "C:");
        Assert.Equal(3, r.JournalFramesApplied);
        Assert.Equal(1, r.FileCount);
    }
}
