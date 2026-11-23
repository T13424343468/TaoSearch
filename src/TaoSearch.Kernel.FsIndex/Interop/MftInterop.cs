using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TaoSearch.Kernel.FsIndex.Interop;

/// <summary>
/// NTFS MFT/USN 直读互操作（架构 §4.1）。
/// 仅 Windows 可运行；Linux 沙箱只做编译验证，运行验证走云端 Windows 流水线（架构 §10.2）。
/// </summary>
internal static class MftInterop
{
    private const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileWRaw(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport(Kernel32, SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    /// <summary>
    /// 打开路径并精确捕获 Win32 错误码：SafeFileHandle 封送器在返回前可能覆盖 LastError，
    /// 故走裸句柄 P/Invoke，先取错误码再包 SafeFileHandle（MFT 枚举失败降级判定依赖准确错误码）。
    /// </summary>
    internal static SafeFileHandle OpenPath(string path, uint desiredAccess, uint shareMode, uint flags, out int win32Error)
    {
        IntPtr raw = CreateFileWRaw(path, desiredAccess, shareMode, 0, OpenExisting, flags, 0);
        win32Error = Marshal.GetLastWin32Error();
        return new SafeFileHandle(raw, true);
    }

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        nint lpInBuffer, uint nInBufferSize,
        nint lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    // FSCTL 控制码 = CTL_CODE(FILE_DEVICE_FILE_SYSTEM=9, 功能号, Method, Access)（winioctl.h/ntifs.h 权威值，2026-11-23 头文件核对）：
    // ENUM=功能号44 METHOD_NEITHER=0x000900B3（原 0x000900b4 差 1，为未定义码恒 err=1；中间误改 0x00090168 亦未定义）
    // READ=功能号46 METHOD_NEITHER=0x000900bb、CREATE=功能号57 METHOD_NEITHER=0x000900e7（原值本就正确，曾误改已回退）
    // QUERY=功能号61 METHOD_BUFFERED=0x000900f4
    // FSCTL_ENUM_USN_DATA：MFT 全量枚举（首建路径）
    internal const uint FsctlEnumUsnData = 0x000900b3;
    // FSCTL_READ_USN_JOURNAL：增量读取（监听路径）
    internal const uint FsctlReadUsnJournal = 0x000900bb;
    // FSCTL_CREATE_USN_JOURNAL
    internal const uint FsctlCreateUsnJournal = 0x000900e7;
    // FSCTL_QUERY_USN_JOURNAL
    internal const uint FsctlQueryUsnJournal = 0x000900f4;

    internal const uint GenericRead = 0x80000000;
    internal const uint ShareRead = 0x1 | 0x2; // FILE_SHARE_READ | FILE_SHARE_WRITE
    internal const uint OpenExisting = 3;
    internal const uint BackupSemantics = 0x02000000;

    /// <summary>
    /// MFT 枚举入参（native MFT_ENUM_DATA_V0，24 字节：StartFileReferenceNumber/LowUsn/HighUsn 各 8 字节）。
    /// 原误写 12 字节混合布局（8 字节引用号 + 两个 USHORT「版本号」）——该布局不存在，
    /// 输入尺寸校验不过 FSCTL 恒返回错误 1（2026-11-23 云端 runner 真卷探针实测：12 字节在全部组合、两种句柄上均 err=1）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct MftEnumInput
    {
        public ulong StartFileReference;
        public ulong LowUsn;      // 0 = 枚举下界
        public ulong HighUsn;     // ulong.MaxValue = 全量枚举上界
    }

    /// <summary>USN 记录头（V2，实际记录为变长，FileName 偏移按结构体计算）。
    /// RecordLength 为 DWORD（4 字节，原误写 ushort 靠记录长度小于 64K 的巧合读对低 16 位）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct UsnRecordV2
    {
        public uint RecordLength;
        public ushort MajorMinorVersion;
        public ulong FileReferenceNumber;
        public ulong ParentFileReferenceNumber;
        public long Usn;
        public long TimeStamp;
        public uint Reason;
        public uint SourceInfo;
        public uint SecurityId;
        public uint FileAttributes;
        public ushort FileNameLength;   // 字节数（UTF-16）
        public ushort FileNameOffset;
        // 后接 FileName（UTF-16LE）
    }

    // Reason 位（USN 增量事件归一用）
    internal const uint ReasonFileCreate = 0x00000100;
    internal const uint ReasonFileDelete = 0x00000200;
    internal const uint ReasonRenameNewName = 0x00004000;
    internal const uint ReasonRenameOldName = 0x00008000;
    internal const uint ReasonDataOverwrite = 0x00000001;
    internal const uint ReasonDataExtend = 0x00000002;
    internal const uint ReasonDataTruncate = 0x00000004;
}
