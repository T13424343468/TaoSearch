using TaoSearch.Kernel.Contracts;
using System.Buffers.Binary;
using System.Text;
using TaoSearch.Kernel.FullText;
using Xunit;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// 分支覆盖补测（测试规范九.11）：FullTextPersistence 断点 Replace/无快照恢复、
/// FullTextJournal 重放坏帧全路径、ThrottledWriteStream 预算耗尽与参数校验。
/// </summary>
public sealed class FullTextBranchTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "taosearch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static byte[] TextPayload(long docId, string text, bool badTextLen = false, int? overrideLen = null)
    {
        var textBytes = Encoding.UTF8.GetBytes(text);
        var payload = new byte[24 + textBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0), docId);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), DateTimeOffset.UnixEpoch.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), badTextLen ? textBytes.Length + 10 : textBytes.Length);
        textBytes.CopyTo(payload, 24);
        if (overrideLen.HasValue)
        {
            // 截短 payload 模拟 len<24 / textBytes 越界
            var trimmed = new byte[overrideLen.Value];
            Array.Copy(payload, trimmed, Math.Min(trimmed.Length, payload.Length));
            return trimmed;
        }
        return payload;
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

    // ---- FullTextPersistence ----

    [Fact]
    public void SaveCheckpoint_Twice_SecondUsesReplace()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "ft.snap");
        using var journal = new MemoryStream();
        var index = new InvertedIndex();
        index.AddDocument(1, "hello world", DateTimeOffset.UnixEpoch);

        FullTextPersistence.SaveCheckpoint(index, snap, journal);
        Assert.True(File.Exists(snap));
        Assert.Equal(0, journal.Length); // 轮转清空

        index.AddDocument(2, "another doc", DateTimeOffset.UnixEpoch);
        FullTextPersistence.SaveCheckpoint(index, snap, journal);
        Assert.Equal(0, journal.Length);
    }

    [Fact]
    public void Restore_NoSnapshot_EmptyIndexAppliesJournal()
    {
        string dir = NewTempDir();
        using var journal = new MemoryStream();
        var (index, frames, torn) = FullTextPersistence.Restore(Path.Combine(dir, "none.snap"), journal);
        Assert.Equal(0, index.DocCount);
        Assert.Equal(0, frames);
        Assert.Equal(0, torn);
    }

    [Fact]
    public void Restore_WithSnapshotAndJournal_Replays()
    {
        string dir = NewTempDir();
        string snap = Path.Combine(dir, "ft.snap");
        var index = new InvertedIndex();
        index.AddDocument(1, "hello", DateTimeOffset.UnixEpoch);
        using (var journal = new MemoryStream())
            FullTextPersistence.SaveCheckpoint(index, snap, journal);

        using var replayJournal = new MemoryStream();
        FullTextJournal.Append(replayJournal, new FullTextEvent(2, "world", DateTimeOffset.UnixEpoch));
        FullTextJournal.Append(replayJournal, new FullTextEvent(1, null, default));
        replayJournal.Position = 0;
        var (restored, frames, _) = FullTextPersistence.Restore(snap, replayJournal);
        Assert.Equal(2, frames);
        Assert.Equal(1, restored.DocCount);
        Assert.Empty(restored.Lookup("hello"));
        Assert.NotEmpty(restored.Lookup("world"));
    }

    [Fact]
    public void Restore_NullTextEvent_RemovesDocument()
    {
        using var journal = new MemoryStream();
        FullTextJournal.Append(journal, new FullTextEvent(5, "abc", DateTimeOffset.UnixEpoch));
        FullTextJournal.Append(journal, new FullTextEvent(5, null, default));
        journal.Position = 0;
        var (index, frames, _) = FullTextPersistence.Restore(Path.Combine(NewTempDir(), "none"), journal);
        Assert.Equal(2, frames);
        Assert.Equal(0, index.DocCount);
    }

    // ---- FullTextJournal.Replay 坏帧路径 ----

    [Fact]
    public void JournalReplay_BadOp_StopsUnclean()
    {
        var bytes = new byte[] { 9 };
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
        Assert.Equal(0, r.FramesApplied);
    }

    [Fact]
    public void JournalReplay_TruncatedLen_StopsUnclean()
    {
        var bytes = new byte[] { 1, 8, 0 };
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_NegativeLen_StopsUnclean()
    {
        var bytes = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0xFF };
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_OversizeLen_StopsUnclean()
    {
        var bytes = new byte[] { 1, 1, 0, 0x10, 0 };
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_TruncatedPayload_StopsUnclean()
    {
        var bytes = Frame(1, new byte[24])[..^10];
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_TruncatedCrc_StopsUnclean()
    {
        var bytes = Frame(1, new byte[24])[..^2];
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_CrcMismatch_StopsUnclean()
    {
        var bytes = Frame(1, new byte[24], corruptCrc: true);
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_UpsertLenBelow24_StopsUnclean()
    {
        var bytes = Frame(1, new byte[20]);
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_UpsertextBytesNegative_StopsUnclean()
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), -1);
        var bytes = Frame(1, payload);
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_UpsertextBytesBeyondPayload_StopsUnclean()
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 50);
        var bytes = Frame(1, payload);
        var r = FullTextJournal.Replay(new MemoryStream(bytes), _ => { });
        Assert.False(r.CleanEnd);
    }

    [Fact]
    public void JournalReplay_NonSeekable_TailZero()
    {
        using var ms = new MemoryStream();
        FullTextJournal.Append(ms, new FullTextEvent(1, "x", DateTimeOffset.UnixEpoch));
        ms.Position = 0;
        var r = FullTextJournal.Replay(new NonSeekableStream(ms), _ => { });
        Assert.Equal(1, r.FramesApplied);
        Assert.Equal(0, r.TruncatedTailBytes);
    }

    // ---- ThrottledWriteStream ----

    [Fact]
    public void Throttle_NonWritableInner_Throws()
        => Assert.Throws<ArgumentException>(() =>
            new ThrottledWriteStream(new MemoryStream(new byte[0], writable: false)));

    [Fact]
    public void Throttle_NonPositiveRate_Throws()
    {
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThrottledWriteStream(ms, 0));
    }

    [Fact]
    public void Throttle_Write_InvalidArgs_Throw()
    {
        using var ms = new MemoryStream();
        using var s = new ThrottledWriteStream(ms, 1 << 20);
        Assert.Throws<ArgumentNullException>(() => s.Write(null!, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Write(new byte[4], -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => s.Write(new byte[4], 0, -1));
        Assert.Throws<ArgumentException>(() => s.Write(new byte[4], 2, 4));
        Assert.Throws<NotSupportedException>(() => _ = s.Length);
        Assert.Throws<NotSupportedException>(() => s.Position = 0);
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => s.SetLength(0));
        Assert.Throws<NotSupportedException>(() => s.Read(new byte[1], 0, 1));
        Assert.False(s.CanRead);
        Assert.False(s.CanSeek);
        Assert.True(s.CanWrite);
    }

    [Fact]
    public void Throttle_Write_BudgetExhaustion_StillWritesAll()
    {
        var ms = new MemoryStream();
        // 64KB/s：写 100KB 必然耗尽首窗预算并进入补窗循环
        using (var s = new ThrottledWriteStream(ms, 64 * 1024, leaveOpen: true))
        {
            var data = new byte[100 * 1024];
            new Random(7).NextBytes(data);
            s.Write(data, 0, data.Length);
            s.Flush();
        }
        Assert.Equal(100 * 1024, ms.Length);
    }

    [Fact]
    public async Task Throttle_WriteAsync_BudgetExhaustion_StillWritesAll()
    {
        var ms = new MemoryStream();
        await using (var s = new ThrottledWriteStream(ms, 32 * 1024, leaveOpen: true))
        {
            var data = new byte[48 * 1024];
            new Random(8).NextBytes(data);
            await s.WriteAsync(data, 0, data.Length, CancellationToken.None);
            await s.FlushAsync(CancellationToken.None);
        }
        Assert.Equal(48 * 1024, ms.Length);
    }

    [Fact]
    public async Task Throttle_WriteAsync_InvalidArgs_Throw()
    {
        using var ms = new MemoryStream();
        using var s = new ThrottledWriteStream(ms, 1 << 20);
        await Assert.ThrowsAsync<ArgumentNullException>(() => s.WriteAsync(null!, 0, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.WriteAsync(new byte[4], -1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => s.WriteAsync(new byte[4], 0, -1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => s.WriteAsync(new byte[4], 2, 4, CancellationToken.None));
    }

    [Fact]
    public void Throttle_LeaveOpen_KeepsInnerAlive()
    {
        var ms = new MemoryStream();
        using (var s = new ThrottledWriteStream(ms, 1 << 20, leaveOpen: true))
            s.Write(new byte[] { 1, 2, 3 }, 0, 3);
        ms.WriteByte(4); // 内层流未关闭，仍可写
        Assert.Equal(4, ms.Length);
    }
}
