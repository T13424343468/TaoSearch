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
    public const int Win32ErrorInvalidFunction = 1;

    private const int InitialBufferBytes = 1 << 20;   // 1MB 起步
    private const int MaxBufferBytes = 16 << 20;      // 16MB 封顶
    private const uint MetafileMaxRecordNumber = 16;  // MFT 前 16 条为系统元文件（含根目录 rec 5），不入索引

    public delegate void RecordSink(long fileReference, long parentReference, string fileName);

    /// <summary>
    /// 卷级 FSCTL（FSCTL_ENUM_USN_DATA / QUERY·READ_USN_JOURNAL）需要卷设备句柄（\\.\C:），
    /// 目录句柄上返回 ERROR_INVALID_FUNCTION(1)（2026-11-23 云端 runner 真卷实测）。
    /// 把任意路径映射到其所在卷的设备路径；UNC 等无法映射的原样返回（打开失败由引擎降级慢路径）。
    /// </summary>
    public static string ToVolumeDevicePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        try
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            if (root.Length >= 2 && root[1] == ':')
                return @"\\.\" + root[0] + ":";
        }
        catch (Exception)
        {
            // 非法路径：原样返回，由打开失败统一降级
        }
        return path;
    }

    public unsafe MftScanOutcome Scan(string volumeRoot, RecordSink sink, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // MFT 全量枚举是整卷语义（枚举整个 NTFS 卷的记录），仅适用于卷根（如 "C:\"）；
        // 子目录根交由引擎降级目录递归慢路径（云端 runner 真卷实测：目录句柄上 FSCTL 返回错误 1）。
        string trimmed = volumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(volumeRoot) ?? string.Empty;
        bool isVolumeRoot = root.Length >= 2 && root[1] == ':'
            && string.Equals(trimmed, root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (!isVolumeRoot)
            return MftScanOutcome.Fail(Win32ErrorInvalidFunction);

        using var volume = MftInterop.OpenPath(
            ToVolumeDevicePath(volumeRoot), MftInterop.GenericRead, MftInterop.ShareRead, MftInterop.BackupSemantics, out int openError);
        if (volume.IsInvalid)
            return MftScanOutcome.Fail(openError != 0 ? openError : Win32ErrorAccessDenied);

        byte[] buffer = new byte[InitialBufferBytes];
        // 探针实测（2026-11-23 云端 runner）：usn=(0,0) 成功返回记录、(0,MAX) 报 38——全量枚举用 (0,0)（无 USN 过滤）
        var input = new MftInterop.MftEnumInput { StartFileReference = 0, LowUsn = 0, HighUsn = 0 };
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
            while (offset + sizeof(uint) <= returned)
            {
                uint recordLength = BitConverter.ToUInt32(buffer, offset);
                if (recordLength == 0 || offset + recordLength > returned) break;

                var record = MemoryMarshal.Read<MftInterop.UsnRecordV2>(buffer.AsSpan(offset));
                if ((uint)record.FileReferenceNumber > MetafileMaxRecordNumber)
                {
                    string name = Encoding.Unicode.GetString(buffer, offset + record.FileNameOffset, record.FileNameLength);
                    sink((long)record.FileReferenceNumber, (long)record.ParentFileReferenceNumber, name);
                }
                recordCount++;
                offset += (int)recordLength;
            }

            // 续扫：下一批从 nextRef 开始；原地不动则终止防死循环
            if (nextRef == 0 || (ulong)nextRef == input.StartFileReference) break;
            input.StartFileReference = (ulong)nextRef;
        }

        sw.Stop();
        return MftScanOutcome.Ok(recordCount, sw.ElapsedMilliseconds);
    }
}
