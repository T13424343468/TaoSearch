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
4. 零星 50% 分支：FsIndexEngine Dispose catch 支路、HotTermCache LRU 空链路、Tokenizer CJK 边界 2 支、InvertedIndex 查询组合支路。

## 跑法备忘
- `mkdir -p /run/user/0`；`export DOTNET_ROOT=/root/workspace/.local/dotnet10 DOTNET_ROLL_FORWARD=LatestMajor`，PATH 前置 dotnet10。
- `dotnet test tests/TaoSearch.Kernel.Tests/... --collect:"XPlat Code Coverage;Format=cobertura"`，账在 TestResults/*/coverage.cobertura.xml。
- 测试文件 UTF-8 带 BOM；只加 tests/ 不改 src/。
