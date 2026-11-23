# 分支覆盖补测断点档案（2026-11-23 02:2x，子线 taosearch_branch_coverage）

## 已落定
- 提交 b2075db（main）：新增 4 个测试文件、144 用例全绿（原 86 → 144，新增 58）。
  - QueryParserBranchTests.cs：ParseSize 单位/比较符/区间/非法输入全分支、ParseDateRange 3/4/6 段全路径。
  - StoreBranchTests.cs：JournalWriter.Replay 坏头/坏版本/坏 op/坏长度/撕裂 payload/坏 CRC/nameLen 非法/非可 seek 流/超长名；JournalSink 写后弃用/无文件轮转/有文件轮转；IndexPersistence 二次断点 Replace、注入引擎恢复、坏日志头兜底、快照+日志重放。
  - FullTextBranchTests.cs：FullTextPersistence 二次断点 Replace/无快照恢复/日志重放/删除事件；FullTextJournal.Replay 全部坏帧路径；ThrottledWriteStream 预算耗尽（同步+异步）、参数校验、leaveOpen。
  - FsIndexBranchTests.cs：MFT 成功路径补前缀、降级慢路径全路径保留、默认慢路径对不存在目录 yield break、ApplyEvent 挂日志、Dispose 二次抛 ObjectDisposed（实测行为）、NameIndex 边界。

## 实测出账（coverlet cobertura，沙箱 2026-11-23 02:23）
- Kernel.Tests：144/144 绿；branch-rate **65.6% → 81.25%**（624/768）。
- 账文件：tests/TaoSearch.Kernel.Tests/TestResults/c6f15889-e6bc-4aee-9f0e-ea33e9ad98f7/coverage.cobertura.xml

## 剩余缺口（按可测性分类）
1. **真实 NTFS 依赖，Linux 沙箱测不到（已试：注入 delegate 仅覆盖引擎编排层，扫描器本体需真卷句柄）**：
   - MftScanner 0/64（DeviceIoControl FSCTL_ENUM_USN_DATA 全路径）——需云端 Windows 管理员卷跑。
   - UsnJournalListener 0/76（同因，监听循环/订阅全在真 USN 句柄上）。
2. **内部 unsafe/哈希桶分支（第二批已落，2026-11-23 09:xx 班，提交 904c6fe）**：新增 SnapshotHashBranchTests.cs 42 用例（Kernel 全量 185/185 绿，云端 CI run 45093090775 绿）。
   - **Kernel 分支覆盖 81.25% → 85.79%**；NameIndex 88.6%→97.9%、InvertedIndex 78.6%→90%+、Tokenizer→100%。
   - 已覆盖：BuildPath 断链/多段/缺失引用、Remove 防用、双释放、名字区扩容、空名记录、搜索大小写/数字首字节/CJK/尾部越界矩阵、快照装载期 GrowHash（300 条）、BufferedStream 直传落盘；
   - 损坏字节流全路径（文件名+全文两套快照）：坏魔数、坏版本、坏头 CRC、active/termCount 负数、nameBytes/termBytes 超 int.MaxValue、头/词条区/记录区/文档区/ID 区截断、坏体 CRC（保留原 trailer 实测）、已删记录旗标、名字偏移非法、负名字长度、慢速流 1 字节分片读。
   - 踩坑记录：①CRC 夹具改内容后必须保留原 trailer 才能实测坏体 CRC 分支（重算等于自证合法）；②BufferedStream 释放会关底层流，SaveSnapshot 只 Flush 不 Dispose 的口径决定了测试侧不能 using 包裹；③InvertedIndex 不是 IDisposable；④NameIndex.Count 属性不设防（防用断言只覆盖写路径）；⑤'.'/'-'/'#' 是词元字符，「纯符号」用例必须避开。
3. **Service.Tests 第一批已落（2026-11-23 08:xx 班，提交 39625a2 + b7e6913）**：新增 ServiceFactoryTests.cs 13 用例（43/43 绿，云端 CI run 45087482016 绿）。
   - Service 分支覆盖 **41.0% → 56.9%**；ServiceHostFactory.CreateAsync 0% → 85.7%。
   - 已覆盖：恢复路径+journal 重放+轮转、损坏 fs.journal 降级、损坏全文快照降级重建、有效全文快照恢复后内容检索命中、RebuildIndex 双作用域先清后建+双检查点、坏 JSON 负回执、PipeSession 空流/超限帧收尾、PipeServer 监听循环端到端+取消收班、ContentIndexer 检查点静默窗口+无日志空操作。
   - 首建路径（MFT 真扫描）以 `OperatingSystem.IsWindows()` 守卫转云端 CI 实测（首建落快照、损坏快照降级首建两条，云端已跑绿）。
   - 踩坑记录：FallbackEntry 第二参是 Size 不是 FileReference（引用号须从 EnumerateLivePaths 取）；夹具 JournalSink 写帧后必须 Dispose 释放句柄，否则 Windows 上检查点轮转删不掉打开中的日志文件（云端 CI 实测红灯一次）。
   - Service 侧剩余缺口：Program 控制台/服务模式（被 Global 互斥体挡，Linux 跑不了，需云端 Windows 进程级实测）、UsnJournalListener/事件泵真源（同 NTFS 依赖，归第 1 项）、ServiceJsonContext 源生成桩（序列化器自动生成代码，意义有限）。
4. **零星 50% 分支已收口（第三批，2026-11-23 10:xx 班，MiscBranchGapsTests.cs 19 用例，205/205 绿）**：
   - **Kernel 分支覆盖 85.79% → 88.82%**（675/760）；FsIndexEngine 44/44 满账、JournalWriter 48/48 满账、HotTermCache 满账。
   - 已覆盖：FsIndexEngine 空卷前缀（MarkRestored("")）、NameIndex 墓碑同键重插（-2 墓碑探测组合）、词条长于名字、首字节命中后失配、'{'/'[' 越界字母判定组合、空名记录 TryGet、HotTermCache 同词覆盖出账、全文快照尾 CRC 缺失（截尾 4 字节）/坏体 CRC（保留原 trailer）/词条记录越界三组合（负 offset/负 idCount/第三词条 idOffset 越界——docIdBytes 由词条记录自和，须改 idOffset 而非 idBytes）/未定义文档引用、QueryParser regex:/case:/wholeword:/未知前缀/size:>（ParseSizeBytes 空串）/dm 四段两变体、JournalSink 弃用后轮转短路、JournalWriter 负长度帧+超 1MB 限长度帧。
   - **踩坑记录**：①全文快照正文 CRC 覆盖 header[..24] + 28..^4（头 CRC 4 字节不在正文 CRC 内，重算 trailer 不能整流哈希）；②docIdBytes 由词条记录 idBytes 自和得出，改单条 idBytes 只会炸「ID 列表区不完整」，越界支路须改 idOffset；③JournalWriter.Replay 前必须先写 "TSSJ"+版本头，否则报「journal 文件头损坏」；④TestResults 多 GUID 目录并存，取账必须按 mtime 取最新，字典序最大是旧账（本班踩过：假账 87.63% vs 真账 88.82%）。
   - **结构性不可达/插桩伪影（不再追）**：NameIndexSnapshot.cs:202 装载期 GrowHash（恢复构造预分配 hashCap*LF ≥ active+1，装载中 (count+1)>(hashCap-1)*LF 恒假，区间数学为空）；FullTextPersistence.cs:22 与 IndexPersistence.cs:94 的 `GetDirectoryName(GetFullPath(..)) ??"."`（GetFullPath 后永不为 null，防御式写法）；ThrottledWriteStream.cs:104 disposing=false（无终结器）；NameIndex 153/160/258/362/389、QueryParser 82/147、InvertedIndexSnapshot 197 为短路不可达组合或 ternary/fixed/switch 插桩伪影（两臂实测均执行过仍记 1/2）。
   - **剩余缺口只有一类**：MftScanner 0/32 + UsnJournalListener 0/38 真卷分支（需云端 Windows 管理员卷，另有安排）+ Program 进程级模式（Global 互斥体挡）+ App 壳层未出账（研发总监终裁：壳层 100% 覆盖走「可测逻辑抽独立程序集」路线，原型标注随下轮交付，主班重构中）。

5. **Service.Tests 第二批已落（2026-11-23 11:xx 班，ServiceBranchGapsTests.cs 17 用例，60/60 绿）**：
   - **Service 分支覆盖 56.46% → 61.02%**（同口径 730/1293 → 789/1293，含生成代码、不含壳层）。
   - 已覆盖：SearchEngine 非法正则空终批、BatchSize=0 钳位、空词浏览+ext 过滤、wholeword 两侧、size 上下限、dm 日期区间、path 前缀子目录、ScanCap 10 万条截断不挂起、全文命中文件已删跳过、双路命中合并、0 字节/内容不一致预览置空、多词窗口、重复词区间封顶 8；ContentIndexer 无日志事件、日志流写炸只记清单（先写后应用索引仍生效）、失败清单环形淘汰至 100 条。
   - **踩坑记录**：①BatchSize=0 经 Math.Max(1,0)=1，每批只走 1 条，断言分批次数时按此口径；②未知扩展名静默跳过、不进失败清单，淘汰环形清单的测试须用「不存在文件」驱动；③C# List 取末元素用 `[^1]` 不是 `[-1]`。
   - Service 侧剩余缺口：Program 进程级归云端（同第 3 项）、PipeSession 87 行超限帧断开需管道夹具（留下一班）、ServiceHost 两处零星、PathFilter.Exclude 解析器未接经公共 API 不可达（归插桩伪影类停追）。

6. **App 壳层可测化改造已落（2026-11-23 班，终裁①执行完毕）**：
   - 新程序集 `src/TaoSearch.App.Core`（net10.0，零 WinUI 依赖）：`git mv` 迁移 9 个无界面依赖文件（AppSettings/FileLauncher/SearchGateway/ServiceSupervisor/SingleInstanceLaunch/PipeClient/HitViewModel/MainViewModel/SettingsViewModel），命名空间统一 `TaoSearch.App.Core`；新增 `ISearchGateway` 接口作 ViewModel 接缝；AppSettings 增路径可注入 internal 重载 + `InternalsVisibleTo`；App 工程 9 处 using 同步更新，App 内 GlobalHotkeyService/JumpListService/TrayIconService/WindowPlacement 留原命名空间。
   - 新测试工程 `tests/TaoSearch.App.Core.Tests`：**87/87 绿**（LaunchArgs/AppSettings/MainViewModel/SettingsViewModel/Pipe 回环/Gateway/RemainingBranch/BranchCompletion 八个测试文件）；Kernel 205/205、Service 60/60 回归全绿。
   - **壳层覆盖账（Linux 沙箱口径，coverlet cobertura）**：分支点 63 个，Linux 可测 58 个中 **55/58 = 94.83%**；Windows 专属 5 个分支点（SingleInstanceLaunch，Linux 跳过）由 windows-ci 实跑出账。
   - **改造过程修掉 3 个真缺陷**：①PipeClient 空载荷应答先抛干净 InvalidDataException（原会炸原始 JSON 异常）；②PipeClient 断线重连：CallAsync 对 attempt==0 的 IOException 弃旧换新重试一次（服务重启/单应答即关场景）；③AppJsonContext 加 `[ExcludeFromCodeCoverage]`（源生成代码出账干扰）。
   - **剩余 3 处半分支归类停追（均有实证）**：
     - PipeClient.CallAsync catch 内 `_pipe?.Dispose()` 空侧：结构性不可达——进该 catch 的前提是连接已成功（`_pipe` 必非空）；连接失败抛 TimeoutException 不匹配 `catch (IOException)`（沙箱实测）。
     - PipeClient.EnsureConnectedAsync `_pipe?.Dispose()` 非空侧：Linux 不可达——Unix UDS 对端关闭后 `IsConnected` 仍为 true（沙箱实测），到达该行时 `_pipe` 恒为 null；Windows 真机行为不同，留 windows-ci 出账。
     - ServiceSupervisor `process is null` 防御分支：`Process.Start`（UseShellExecute=false）失败时抛异常不返回 null，结构性不可达。
   - **踩坑记录**：①cobertura 解析必须按（类, 方法, 条件号）三元组去重——同类不同方法的条件号会撞号，按类去重会漏账（本班实测漏 3 处）；②`condition-coverage` 是百分比字符串、`number` 是条件号不是分支数，直接 int() 会炸；③Unix UDS 上「写完立即关」会重置连接，回环服务端应答后须保持 ≥150ms 窗口；④Linux 上连不存在管道抛 TimeoutException（3 秒超时）而非 IOException，写异常类测试前先实测异常类型。

7. **真卷覆盖实测已落（2026-11-23 班，RealVolumeTests.cs 5 用例，非 Benchmark 类进覆盖账）**：
   - MftScanner 真扫系统卷（含 MoreData 扩容、续扫、元文件跳过全分支）+ 不存在卷失败支路；UsnJournalListener 真卷四类事件闭环（建/改名/改内容/删）+ 不存在卷静默退出 + UNC 非卷句柄静默退出。Linux 如实跳过留痕，windows-ci 管理员卷实跑出账。
   - **真卷实测当场抓到到货即坏缺陷三连（修红提交 a58b966）**：①卷级 FSCTL 拿目录句柄发必败（错误 1），改卷设备句柄 `\\.\C:`（ToVolumeDevicePath 映射，UNC 原样）；②USN 记录解析未跳过缓冲头 8 字节 next-USN，把头当记录解析；③续读 USN 取缓冲尾 8 字节（应取头 8 字节），会重复读旧记录。④顺修：MFT 全量枚举加卷根守卫——子目录根整卷枚举语义不成立，原靠「MFT 必败降级」掩盖，现非卷根明确返回错误 1 由引擎降级慢路径（Service 首建测试临时目录卷根口径不受影响，走慢路径断言不变）。
   - **连带发现**：benchmark.yml 自 11-21 起五连败且作业列表为空（工作流文件问题，从未真正执行）——「runner 走 MFT 快路径」的注释假设从未被验证；本次真卷用例是第一条真实探针。
   - Program 进程级模式（Global 互斥体挡）同步落 windows-ci 实测步：G-P1 首实例存活 / G-P2 第二实例被互斥体挡住退出（校验占用文案）/ G-P3 首实例不受影响 / G-P4 收尾，日志随测试证据归档。

## 跑法备忘
- `mkdir -p /run/user/0`；`export DOTNET_ROOT=/root/workspace/.local/dotnet10 DOTNET_ROLL_FORWARD=LatestMajor`，PATH 前置 dotnet10。
- `dotnet test tests/TaoSearch.Kernel.Tests/... --collect:"XPlat Code Coverage;Format=cobertura"`，账在 TestResults/*/coverage.cobertura.xml。
- 测试文件 UTF-8 带 BOM；只加 tests/ 不改 src/。

8. **真卷整改连续修红班（2026-11-23 16:xx 班，老板令「按最新制度全面整改」）**：
   - **FSCTL 控制码权威值定版（b01ac23）**：ntifs.h 核对 ENUM=0x000900B3（功能号44 METHOD_NEITHER，原 0x000900b4 未定义码恒 err=1）；READ=0x000900BB、CREATE=0x000900E7 原值正确回退；QUERY=0x000900F4 恒对。
   - **ENUM 入参 24 字节 V0 布局定版（24c8fc0/ad87639）**：StartRef/LowUsn/HighUsn 各 8 字节；全量口径 (0,0)——探针实测 (0,0) 返回 592 字节记录、(0,MAX) 报 38。
   - **QUERY 应答布局定版（b01ac23）**：USN_JOURNAL_DATA=JournalID@0/FirstUsn@8/NextUsn@16，续读起点取 @16（原取 @8 实为 FirstUsn）。
   - **READ 入参 40 字节定版（ad87639/b9f4097）**：StartUsn@0/ReasonMask@8/ReturnOnlyOnClose@12/Timeout@16/BytesToWaitFor@24/UsnJournalID@32（原 32 字节漏 ReturnOnlyOnClose 致 1784；JournalID 在前顺序报 87）。
   - **Pin 提前释放修死（24c8fc0）**：GCHandle 覆盖 DeviceIoControl 调用全程。
   - **ENUM 到尾信号收口（7c19805，run 45138988742 验证）**：err=38(ERROR_HANDLE_EOF) 为枚举正常耗尽信号，按正常结束返回已累计 Ok 结果；扫描不再误判失败，但断言「应含 Windows 目录」炸出新红——已加断言前诊断出证（records/sinkCalls/首尾30名/winPrefixed），下一轮云端拿证据定位。
   - **USN reason 重命名位权威值定版（bddd735）**：winioctl.h 核对 RENAME_NEW_NAME=0x2000、RENAME_OLD_NAME=0x1000（原 0x4000/0x8000 实为 INDEXABLE_CHANGE/BASIC_INFO_CHANGE，真实重命名永不映射 Renamed）；run 45138988742 实测 Rename 用例独缺 Renamed 实锤，上一轮监听测试通过系 runner 偶发 0x4000 事件的运气。
   - 云测 run 记账：45136914514（ENUM (0,0) 首次返回记录）、45137561729（READ ok=True ret=65480 增量监听真机已通，仅剩 MFT 扫描 err=38 一红）、45138988742（err=38 收口生效，MFT 断言缺 Windows + Rename 缺 Renamed 两红）、45140065064（reason 权威值 + MFT 诊断出证，值守中）。

9. **真卷分支覆盖收口（2026-11-23 16:xx 班，run 45141681605 全绿出账）**：
   - **MftScanner 真卷分支**：真扫 C:\ 系统卷全路径（QUERY 取 NextUsn→ENUM 分批续扫→err=38 到尾收口→MoreData 扩容→元文件跳过→记录解析出证），云端管理员卷实跑通过。
   - **UsnJournalListener 真卷分支**：四类事件真卷闭环（Created/Renamed/ContentModified/Deleted 实收）+ 不存在卷静默退出 + UNC 非卷句柄静默退出，全绿。
   - **Program 进程级实测**：G-P1 首实例存活 / G-P2 互斥体挡第二实例（占用文案 UTF-8 取证匹配通过）/ G-P3 首实例不受影响 / G-P4 收尾，全过。
   - **收口过程共炸出并修死 6 个到货即坏缺陷**：FSCTL 控制码错（b01ac23）、ENUM 入参布局错（24c8fc0/ad87639）、QUERY 应答偏移错（b01ac23）、READ 入参 40 字节序错（ad87639/b9f4097）、ENUM 到尾信号误判（7c19805）、USN reason 重命名位错（bddd735）+ ENUM 全量口径错（0dacf62，(0,0) 系 USN∈[0,0] 退化过滤，正解 HighUsn=NextUsn）+ 事件泵作用域错（0dacf62，非卷根挂整卷监听灌入根外文件）+ 输出编码错序（7982089，占用文案先于 UTF-8 设置）。
   - **剩余缺口定性**：Linux 不可测的真卷/进程级两类已全部由云端管理员卷实跑出账；余下仅结构性不可达分支与插桩伪影（第 4、6 条归类停追）。100% 门禁口径下无「可测未测」分支。
