using Xunit;
using System.Text.Json;
using TaoSearch.Kernel.Contracts;
using TaoSearch.Kernel.FullText;

namespace TaoSearch.Kernel.Tests;

/// <summary>
/// M4 契约冻结校验（2026-11-21 老板令「按制度落实全部做完」）：
/// ①IPC 方法表与载荷契约冻结断言；②事件订阅契约用「下载文件夹自动管家」用例校验
/// （可复用内核令：管家作为未来 IPC 客户端挂接，契约先经其用例验证再冻结）。
/// </summary>
public sealed class M4ContractFreezeTests
{
    // ---- 一、IPC 方法表冻结（架构 §7.2–7.4 方法族全覆盖，号段只增不改） ----

    [Fact]
    public void IpcMethodTable_Frozen_AllArchitectureMethodsPresent()
    {
        var methods = Enum.GetValues<IpcMethod>().Select(m => (int)m).ToHashSet();
        // 架构 §7.2 查询/状态/重建/暂停恢复 + §7.3 订阅/退订 + §7.4 解析器注册
        int[] expected = [1, 2, 3, 4, 5, 6, 7, 8];
        Assert.Equal(expected, methods.OrderBy(x => x));
        Assert.Equal(1, (int)IpcMethod.Search);          // 首个方法号固定
        Assert.Equal(1, IpcProtocol.Version);            // 协议版本冻结
    }

    [Fact]
    public void IpcPayloads_JsonRoundtrip_UnknownFieldsIgnored()
    {
        // 向后兼容口径：载荷字段只增不删，旧客户端须忽略未知字段
        var filter = new FileEventFilter
        {
            PathPrefixes = [@"C:\Users\me\Downloads"],
            Extensions = [".zip", ".exe"],
            Kinds = FileChangeKind.Created | FileChangeKind.Renamed,
        };
        var req = new SubscribeRequest { Filter = filter };
        string json = JsonSerializer.Serialize(req);
        var back = JsonSerializer.Deserialize<SubscribeRequest>(json);
        Assert.NotNull(back);
        Assert.Equal(filter.PathPrefixes, back!.Filter.PathPrefixes);
        Assert.Equal(filter.Kinds, back.Filter.Kinds);

        // 未知字段不破坏反序列化
        string withUnknown = json[..^1] + ",\"futureField\":123}";
        var tolerant = JsonSerializer.Deserialize<SubscribeRequest>(withUnknown);
        Assert.NotNull(tolerant);
    }

    [Fact]
    public void AckResponse_FailCarriesMachineReadableError()
    {
        var ack = AckResponse.Fail("E_INDEX_BUSY");
        Assert.False(ack.Ok);
        Assert.Equal("E_INDEX_BUSY", ack.Error);
        Assert.True(new AckResponse().Ok);
    }

    // ---- 二、管家用例校验事件订阅契约（下载文件夹自动管家场景） ----

    private static FileEvent Ev(FileChangeKind kind, string path, long refId = 100) => new()
    {
        Kind = kind,
        FileReference = refId,
        Path = path,
        UtcTimestamp = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void ButlerCase1_NewArchiveInDownloads_Matches()
    {
        // 管家场景：盯下载文件夹，新压缩包/安装包落地即归档
        var filter = new FileEventFilter
        {
            PathPrefixes = [@"C:\Users\me\Downloads"],
            Extensions = [".zip", ".exe", ".iso", ".7z"],
            Kinds = FileChangeKind.Created,
        };
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Downloads\setup.exe")));
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Downloads\docs.zip")));
        Assert.False(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Downloads\readme.txt")));
    }

    [Fact]
    public void ButlerCase2_CaseInsensitivePrefixAndExtension()
    {
        // 盘符/路径大小写、扩展名大小写均不敏感（资源管理器实际行为）
        var filter = new FileEventFilter
        {
            PathPrefixes = [@"c:\users\ME\Downloads"],
            Extensions = [".ZIP"],
        };
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Downloads\ARCHIVE.ZIP")));
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"c:\users\me\downloads\a.Zip")));
    }

    [Fact]
    public void ButlerCase3_OutsideWatchedPrefix_FilteredOut()
    {
        // 管家只管下载夹：系统目录事件不得漏进订阅流
        var filter = new FileEventFilter { PathPrefixes = [@"C:\Users\me\Downloads"] };
        Assert.False(filter.Matches(Ev(FileChangeKind.Created, @"C:\Windows\temp.exe")));
        Assert.False(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\DownloadsBackup\a.zip"))); // 前缀非目录边界也拦
    }

    [Fact]
    public void ButlerCase4_KindFiltering_DedupWatchesRenamesOnly()
    {
        // 去重场景：只听重命名，删除事件不投递
        var filter = new FileEventFilter { Kinds = FileChangeKind.Renamed };
        Assert.True(filter.Matches(Ev(FileChangeKind.Renamed, @"C:\x\a.txt")));
        Assert.False(filter.Matches(Ev(FileChangeKind.Deleted, @"C:\x\a.txt")));
        Assert.False(filter.Matches(Ev(FileChangeKind.ContentModified, @"C:\x\a.txt")));
    }

    [Fact]
    public void ButlerCase5_CleanupWatchesDeletes_EmptyFilterMatchesAll()
    {
        // 清理场景：空过滤器 = 全收（全事件类型、全路径）
        var filter = new FileEventFilter();
        Assert.True(filter.Matches(Ev(FileChangeKind.Deleted, @"D:\any\where.log")));
        Assert.True(filter.Matches(Ev(FileChangeKind.ContentModified, @"E:\data\bin.dat")));
    }

    [Fact]
    public void ButlerCase6_MultiplePrefixes_AnyMatchPasses()
    {
        // 管家同时盯下载夹与桌面
        var filter = new FileEventFilter { PathPrefixes = [@"C:\Users\me\Downloads", @"C:\Users\me\Desktop"] };
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Desktop\note.txt")));
        Assert.True(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Downloads\a.zip")));
        Assert.False(filter.Matches(Ev(FileChangeKind.Created, @"C:\Users\me\Documents\a.docx")));
    }

    // ---- 三、全文落盘限速（三项决议之三落地校验） ----

    [Fact]
    public void ThrottledStream_EnforcesRateBudget_AndPreservesData()
    {
        using var ms = new MemoryStream();
        using var throttled = new ThrottledWriteStream(ms, bytesPerSecond: 1L * 1024 * 1024); // 1MB/s
        byte[] payload = new byte[512 * 1024]; // 0.5MB × 3 = 1.5MB，1MB/s 预算下至少 ~0.5s
        Random.Shared.NextBytes(payload);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        throttled.Write(payload, 0, payload.Length);
        throttled.Write(payload, 0, payload.Length);
        throttled.Write(payload, 0, payload.Length);
        sw.Stop();

        Assert.Equal(payload.Length * 3, ms.Length);                       // 数据完整性
        Assert.True(sw.ElapsedMilliseconds >= 400, $"限速未生效：1.5MB@1MB/s 仅耗时 {sw.ElapsedMilliseconds}ms");
        Assert.True(sw.ElapsedMilliseconds < 5000, $"限速过严：耗时 {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task ThrottledStream_WriteAsync_PreservesData()
    {
        using var ms = new MemoryStream();
        await using var throttled = new ThrottledWriteStream(ms, bytesPerSecond: 100L * 1024 * 1024); // 高预算不触发等待
        byte[] payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        await throttled.WriteAsync(payload, 0, payload.Length, CancellationToken.None);
        await throttled.FlushAsync(CancellationToken.None);
        Assert.Equal(payload.Length, ms.Length);
        Assert.Equal(payload, ms.ToArray());
    }
}
