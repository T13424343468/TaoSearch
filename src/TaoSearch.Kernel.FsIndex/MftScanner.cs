using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TaoSearch.Kernel.FsIndex.Interop;

namespace TaoSearch.Kernel.FsIndex;

/// <summary>MFT 全量枚举结果（成功含记录数与耗时；失败含 Win32 错误码，5=权限不足）。</summary>
public sealed record MftScanOutcome
{
    public bool Success { get; init; }
    public int Win32Error { get; init; }
    public long RecordCount { get; init; }
    public long ElapsedMilliseconds { get; init; }

    public static MftScanOutcome Ok(long recordCount, long elapsedMs) =>
        new() { Success = true, RecordCount = recordCount, ElapsedMilliseconds = elapsedMs };

    public static MftScanOutcome Fail(int win32Error) => new() { Success = false, Win32Error = win32Error };
}

/// <summary>
/// MFT 全量枚举（FSCTL_ENUM_USN_DATA，架构 §4.1 首建路径）。
/// 输出缓冲布局：前 8 字节为下一批续扫起始引用号，其后为连续变长 USN_RECORD_V2；
/// 缓冲不足按 ERROR_MORE_DATA(234) 扩容后同起点重试；权限不足（ERROR_ACCESS_DENIED=5）原样上报，由引擎降级慢路径。
/// 需管理员或卷读权限（knowledge.md 权限模型）；Linux 沙箱仅编译验证，运行验证走云端 Windows 流水线。
/// </summary>
public sealed class MftScanner
{
    public const int Win32ErrorAccessDenied = 5;
    public const int Win32ErrorMoreData = 234;

    private const int InitialBufferBytes = 1 << 20;   // 1MB 起步
    private const int MaxBufferBytes = 16 << 20;      // 16MB 封顶
    private const uint MetafileMaxRecordNumber = 16;  // MFT 前 16 条为系统元文件（含根目录 rec 5），不入索引

    public delegate void RecordSink(long fileReference, long parentReference, string fileName);

    public unsafe MftScanOutcome Scan(string volumeRoot, RecordSink sink, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        using var volume = MftInterop.OpenPath(
            volumeRoot, MftInterop.GenericRead, MftInterop.ShareRead, MftInterop.BackupSemantics, out int openError);
        if (volume.IsInvalid)
            return MftScanOutcome.Fail(openError != 0 ? openError : Win32ErrorAccessDenied);

        byte[] buffer = new byte[InitialBufferBytes];
        var input = new MftInterop.MftEnumInput { StartFileReference = 0, LowVersion = 2, HighVersion = 2 };
        int inputSize = Marshal.SizeOf<MftInterop.MftEnumInput>();
        var inputBytes = new byte[inputSize];
        long recordCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            MemoryMarshal.Write(inputBytes, in input);

            uint returned;
            bool ok;
            fixed (byte* inPtr = inputBytes, outPtr = buffer)
            {
                ok = MftInterop.DeviceIoControl(
                    volume, MftInterop.FsctlEnumUsnData,
                    (nint)inPtr, (uint)inputBytes.Length,
                    (nint)outPtr, (uint)buffer.Length, out returned, 0);
            }

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == Win32ErrorMoreData && buffer.Length < MaxBufferBytes)
                {
                    buffer = new byte[Math.Min(buffer.Length * 4, MaxBufferBytes)];
                    continue;   // 同一起点重试
                }
                return MftScanOutcome.Fail(err);
            }

            if (returned <= sizeof(long)) break;    // 无更多记录，枚举结束

            long nextRef = BitConverter.ToInt64(buffer, 0);
            int offset = sizeof(long);
            while (offset + sizeof(ushort) <= returned)
            {
                ushort recordLength = BitConverter.ToUInt16(buffer, offset);
                if (recordLength == 0 || offset + recordLength > returned) break;

                var record = MemoryMarshal.Read<MftInterop.UsnRecordV2>(buffer.AsSpan(offset));
                if ((uint)record.FileReferenceNumber > MetafileMaxRecordNumber)
                {
                    string name = Encoding.Unicode.GetString(buffer, offset + record.FileNameOffset, record.FileNameLength);
                    sink((long)record.FileReferenceNumber, (long)record.ParentFileReferenceNumber, name);
                }
                recordCount++;
                offset += recordLength;
            }

            // 续扫：下一批从 nextRef 开始；原地不动则终止防死循环
            if (nextRef == 0 || (ulong)nextRef == input.StartFileReference) break;
            input.StartFileReference = (ulong)nextRef;
        }

        sw.Stop();
        return MftScanOutcome.Ok(recordCount, sw.ElapsedMilliseconds);
    }
}
