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
2. **内部 unsafe/哈希桶分支（NameIndex 89.3%、NameIndexSnapshot 75%、InvertedIndexSnapshot 75.7%）**：需构造哈希环/快照损坏字节流，属下一批用例（可做，未及）。
3. **Service.Tests 未动**：仍 41.0%。Program 控制台模式、ServiceHostFactory 临时目录装配、PipeServer 生命周期为下一批（可做，未及）。
4. 零星 50% 分支：FsIndexEngine Dispose catch 支路、HotTermCache LRU 空链路、Tokenizer CJK 边界 2 支、InvertedIndex 查询组合支路。

## 跑法备忘
- `mkdir -p /run/user/0`；`export DOTNET_ROOT=/root/workspace/.local/dotnet10 DOTNET_ROLL_FORWARD=LatestMajor`，PATH 前置 dotnet10。
- `dotnet test tests/TaoSearch.Kernel.Tests/... --collect:"XPlat Code Coverage;Format=cobertura"`，账在 TestResults/*/coverage.cobertura.xml。
- 测试文件 UTF-8 带 BOM；只加 tests/ 不改 src/。
