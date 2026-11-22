using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaoSearch.App.Services;

/// <summary>
/// 任务栏跳转列表（经典 Shell 自定义目标列表，无包身份依赖）。
/// 任务：快速搜索窗（Alt + Q）→ --quick；打开索引位置 → --open-index；
/// 最近搜索分类：每条 → --search &lt;关键词&gt;。
/// 任何失败降级记 Trace，不阻断启动。
/// </summary>
internal static class JumpListService
{
    /// <summary>与安装器快捷方式 ShortcutProperty(System.AppUserModel.ID) 一致，跳转列表据此挂到任务栏按钮。</summary>
    public const string AppId = "Tao.TaoSearch";

    // —— COM 标识（逐字核验：ReactOS SDK objectarray.idl / propsys.idl、MS 官方文档） ——
    private static readonly Guid ClsidDestinationList = new("77F10CF0-3DB5-4966-B520-212716D7F7E5");   // CLSID_DestinationList
    private static readonly Guid ClsidEnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A"); // CLSID_EnumerableObjectCollection
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");          // CLSID_ShellLink
    private static readonly Guid IidICustomDestinationList = new("6332DEBF-87B5-4670-90C0-5E57B408A4E9");
    private static readonly Guid IidIObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");          // objectarray.idl
    private static readonly Guid IidIObjectCollection = new("5632B1A4-E38A-400A-928A-D4CD63230295");     // objectarray.idl（注意：非 2D3468C1，那是 coclass）
    private static readonly Guid IidIShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidIPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");        // propsys.idl

    // PKEY_Title：任务项显示名（fmtid/pid 见 ShObjIdl 体系属性定义）
    private static readonly PropertyKey KeyTitle = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    /// <summary>进程启动即设置显式 AUMID，与安装器快捷方式属性对齐。</summary>
    public static void SetProcessAppId()
    {
        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppId);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JumpList] SetCurrentProcessExplicitAppUserModelID 失败: {ex.Message}");
        }
    }

    /// <summary>构建跳转列表：任务两项 + 最近搜索分类。失败降级不抛出。</summary>
    public static void Build(IReadOnlyList<string> recentSearches)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var destList = (ICustomDestinationList)Activator.CreateInstance(
                Type.GetTypeFromCLSID(ClsidDestinationList)!)!;
            destList.SetAppID(AppId);

            // 已删除目标：本应用条目均为内部命令链接，无文件跟踪数据，取回即弃
            var removedIid = IidIObjectArray;
            destList.BeginList(out _, ref removedIid, out _);

            var tasks = CreateCollection();
            AddLink(tasks, exePath, "--quick", "快速搜索窗（Alt + Q）");
            AddLink(tasks, exePath, "--open-index", "打开索引位置");
            destList.AddUserTasks(tasks.GetObjectArray());

            if (recentSearches.Count > 0)
            {
                var recents = CreateCollection();
                foreach (var keyword in recentSearches)
                {
                    if (string.IsNullOrWhiteSpace(keyword)) continue;
                    AddLink(recents, exePath, $"--search {Quote(keyword)}", keyword);
                }
                // 自定义分类被系统拒绝（未注册文件关联等）时跳过，不影响任务项
                var hr = destList.AppendCategory("最近搜索", recents.GetObjectArray());
                if (hr != 0)
                    Trace.WriteLine($"[JumpList] AppendCategory(最近搜索) hr=0x{hr:X8}，已跳过");
            }

            destList.CommitList();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[JumpList] 构建失败（不阻断启动）: {ex.Message}");
        }
    }

    private static string Quote(string keyword) =>
        keyword.Contains(' ') ? $"\"{keyword}\"" : keyword;

    private static CollectionAdapter CreateCollection()
    {
        var collection = (IObjectCollection)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ClsidEnumerableObjectCollection)!)!;
        return new CollectionAdapter(collection);
    }

    private static void AddLink(CollectionAdapter collection, string exePath, string arguments, string title)
    {
        var link = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidShellLink)!)!;
        link.SetPath(exePath);
        link.SetArguments(arguments);
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
        link.SetIconLocation(exePath, 0);

        // 任务项显示名来自链接的 System.Title 属性
        var store = (IPropertyStore)link;
        var pv = PropVariant.FromString(title);
        var key = KeyTitle;
        store.SetValue(ref key, ref pv);
        store.Commit();

        collection.AddObject(link);
    }

    // —— 互操作声明（vtable 顺序逐字对齐 ShObjIdl.idl / objectarray.idl） ——

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public PropertyKey(Guid formatId, uint propertyId) { FormatId = formatId; PropertyId = propertyId; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VariantType;   // VT_LPWSTR = 31
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;

        public static PropVariant FromString(string value)
        {
            var pv = new PropVariant { VariantType = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
            return pv;
        }
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        IntPtr GetIDList();
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        short GetHotKey();
        void SetHotKey(short wHotKey);
        uint GetShowCmd();
        void SetShowCmd(uint iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        uint GetCount();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetAt(uint uiIndex, ref Guid riid);
    }

    [ComImport, Guid("5632B1A4-E38A-400A-928A-D4CD63230295"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection : IObjectArray
    {
        new uint GetCount();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        new object GetAt(uint uiIndex, ref Guid riid);
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object punk);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport, Guid("6332DEBF-87B5-4670-90C0-5E57B408A4E9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void BeginList(out uint minSlots, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object removedDestinations);
        [PreserveSig]
        int AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IObjectArray objects);
        void AppendKnownCategory(uint category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object removed);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);
        void AbortList();
    }

    /// <summary>包装 IObjectCollection，对外只暴露 IObjectArray（AppendCategory/AddUserTasks 形参类型）。</summary>
    private sealed class CollectionAdapter
    {
        private readonly IObjectCollection _collection;
        public CollectionAdapter(IObjectCollection collection) => _collection = collection;
        public void AddObject(object punk) => _collection.AddObject(punk);
        public IObjectArray GetObjectArray() => (IObjectArray)_collection;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", PreserveSig = false, SetLastError = false)]
        public static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);
    }
}
