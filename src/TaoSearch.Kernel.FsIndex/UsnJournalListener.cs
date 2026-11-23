using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FsIndex.Interop;

namespace TaoSearch.Kernel.FsIndex;

/// <summary>
/// USN Journal 增量监听（架构 §4.1）：后台线程持续读取变更记录，归一为 FileEvent。
/// Windows 专属运行时；Linux 沙箱编译验证，运行验证走云端 Windows 流水线。
/// </summary>
public sealed class UsnJournalListener : IFileEventSource, IDisposable
{
    private readonly string _volume;
    private readonly Channel<FileEvent> _events;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UsnJournalListener(string volumeRoot, int channelCapacity = 65536)
    {
        _volume = volumeRoot;
        _events = Channel.CreateBounded<FileEvent>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // 监听不阻塞索引主流程
        });
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ListenLoop(_cts.Token));
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        // 打开卷设备句柄（\\.\C:，卷级 FSCTL 必需；目录句柄上 FSCTL 返回错误 1，云端 runner 实测）。
        // 需要管理员或卷读权限；无权限时上层降级慢路径。
        using var volume = MftInterop.CreateFileW(
            MftScanner.ToVolumeDevicePath(_volume), MftInterop.GenericRead, MftInterop.ShareRead,
            0, MftInterop.OpenExisting, MftInterop.BackupSemantics, 0);
        if (volume.IsInvalid) return;

        var journalId = QueryJournal(volume);
        if (journalId is null) return;

        var buffer = new byte[64 * 1024];
        var readArgs = new ReadJournalArgs(journalId.Value.UsnJournalId, journalId.Value.NextUsn);

        while (!ct.IsCancellationRequested)
        {
            int read = ReadUsn(volume, readArgs, buffer);
            if (read <= sizeof(long)) { await Task.Delay(100, ct); continue; }   // 仅 8 字节 next-USN 头 = 无新记录

            // 输出缓冲布局：前 8 字节为下次续读 USN，其后为连续 USN_RECORD_V2
            int offset = sizeof(long);
            while (offset < read)
            {
                var record = MemoryMarshal.Read<MftInterop.UsnRecordV2>(buffer.AsSpan(offset));
                if (record.RecordLength == 0) break;

                var nameBytes = new byte[record.FileNameLength];
                Buffer.BlockCopy(buffer, offset + record.FileNameOffset, nameBytes, 0, record.FileNameLength);
                var fileName = Encoding.Unicode.GetString(nameBytes);

                var kind = MapReason(record.Reason);
                if (kind is not null)
                {
                    await _events.Writer.WriteAsync(new FileEvent
                    {
                        Kind = kind.Value,
                        FileReference = (long)record.FileReferenceNumber,
                        ParentReference = (long)record.ParentFileReferenceNumber,
                        Path = fileName, // 完整路径由 NameIndex 父链重建
                        UtcTimestamp = DateTimeOffset.FromFileTime(record.TimeStamp),
                    }, ct);
                }
                offset += record.RecordLength;
            }
            readArgs.NextUsn = BitConverter.ToInt64(buffer, 0);   // 缓冲头 8 字节 = 下次续读 USN
        }
    }

    public async IAsyncEnumerable<FileEvent> SubscribeAsync(
        FileEventFilter filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in _events.Reader.ReadAllAsync(ct))
        {
            if (Matches(e, filter)) yield return e;
        }
    }

    private static bool Matches(FileEvent e, FileEventFilter f) => f.Matches(e); // 契约层单源（M4 冻结）

    private static FileChangeKind? MapReason(uint reason)
    {
        if ((reason & MftInterop.ReasonFileCreate) != 0) return FileChangeKind.Created;
        if ((reason & MftInterop.ReasonFileDelete) != 0) return FileChangeKind.Deleted;
        if ((reason & MftInterop.ReasonRenameNewName) != 0) return FileChangeKind.Renamed;
        if ((reason & (MftInterop.ReasonDataOverwrite | MftInterop.ReasonDataExtend | MftInterop.ReasonDataTruncate)) != 0)
            return FileChangeKind.ContentModified;
        return null;
    }

    // ---- 互操作薄封装（结构体封送细节，云端 Windows 实测校准） ----
    private (ulong UsnJournalId, long NextUsn)? QueryJournal(SafeFileHandle volume)
    {
        var outBuf = new byte[64];
        var ok = MftInterop.DeviceIoControl(volume, MftInterop.FsctlQueryUsnJournal,
            0, 0, PinBytes(outBuf), (uint)outBuf.Length, out _, 0);
        if (!ok) return null;
        ulong journalId = BitConverter.ToUInt64(outBuf, 8);
        long nextUsn = BitConverter.ToInt64(outBuf, 16);
        return (journalId, nextUsn);
    }

    private int ReadUsn(SafeFileHandle volume, ReadJournalArgs args, byte[] buffer)
    {
        var inBytes = args.ToBytes();
        var ok = MftInterop.DeviceIoControl(volume, MftInterop.FsctlReadUsnJournal,
            PinBytes(inBytes), (uint)inBytes.Length,
            PinBytes(buffer), (uint)buffer.Length, out var returned, 0);
        return ok ? (int)returned : 0;
    }

    /// <summary>临时固定托管数组取指针（调用期间有效，DeviceIoControl 同步返回前不失效）。</summary>
    private static nint PinBytes(byte[] array)
    {
        var h = GCHandle.Alloc(array, GCHandleType.Pinned);
        try { return h.AddrOfPinnedObject(); }
        finally { h.Free(); }
    }

    private sealed class ReadJournalArgs(ulong journalId, long nextUsn)
    {
        public ulong UsnJournalId = journalId;
        public long NextUsn = nextUsn;
        public uint ReasonMask = 0xFFFFFFFF;
        public ulong ReturnOnlyOnClose = 0;
        public ulong Timeout = 0;
        public ulong BytesToWaitFor = 0;
        public uint StartUsn = 1;

        public byte[] ToBytes()
        {
            var b = new byte[8 + 8 + 4 + 8 + 8 + 8 + 4];
            using var ms = new MemoryStream(b);
            using var w = new BinaryWriter(ms);
            w.Write(UsnJournalId); w.Write(NextUsn); w.Write(ReasonMask);
            w.Write(ReturnOnlyOnClose); w.Write(Timeout); w.Write(BytesToWaitFor); w.Write(StartUsn);
            return b;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _loop?.Wait(2000); } catch (AggregateException) { }
        _cts?.Dispose();
    }
}
