# 全量开发令断点档案（2026-11-21 13:2x 接令）

## 令
老板令「全部做完，所有批次」：TaoSearch 剩余批次一次做完——M4 引擎接线、壳层客户端、安装器新标准落地。

## 派工记录
- 分身派工两路均失败（depth_limit_exceeded），按调度制度 §一20 降级链第二级：主班自查补位，三路串并执行，标明「降级自查」。
- 三路：①M4 引擎接线（核心，主班亲写）；②壳层 WinUI3 客户端（主班补位）；③安装器新标准（主班补位，参照 TaoZip 1.0.8 实现）。

## 断点
- 做到哪：主线已归位（efed65f 落档基准班产物），接口清点完毕，准备落码。
- 还差什么：三路代码+测试+云端验证全部未做。
- 下一步动哪：先写引擎接线（SearchService 挂 FsIndexEngine/InvertedIndex/UsnJournalListener 到 8 方法），统一验证后推云端 CI；随后壳层、安装器。

## 进展回填（2026-11-21 13:5x，主线班）
- **批次① M4 引擎接线：代码收口**（m4/engine-wiring c638897，已并 main 46fe918）：
  - SearchEngine/ContentIndexer/ServiceHost/ParserRegistry/Program 全部落码；IndexPersistence.Restore 增可选目标引擎参数。
  - 修 3 真缺陷：零命中不发终帧、yield-in-catch、尺寸过滤器未赋值变量。
  - 新增 tests/TaoSearch.Service.Tests 30 例（SearchEngine 11 + ContentIndexer 8 + IPC 端到端 8 + FrameWriter 3），86/86 全绿，零错零警。
- **撞车处置（二次）**：并行班 SearchService.cs/ServicePayloadContext.cs（未编译）与验证版冲突，已删；TaoSearch.App 暂出 slnx，壳层班收口后回归；windows-ci 壳层编译步暂移（注释留痕）。
- **批次② 壳层班**：在岗，直接推 main 迭代 App 依赖（WASDK 1.8/BuildTools），已发协调消息重申「App 收口后由主线班归并」。
- **批次③ 安装器班**：在岗（taosearch-pack-clone）。
- **待办**：收两班活→归并→云端 CI 全绿→出包。

## 进展回填（2026-11-21 15:1x，主线班·收口）
- **批次① M4 引擎接线：云端 CI 全绿**（main f2c7b81，run 44879425297 success）：
  - NameIndex 时间戳双换算缺陷修复（6c8add8）：写入存 .NET ticks、读侧误用 FILETIME 致偏移 1600 年；读侧改同纪元解读，Rec 字段更名 ModifiedUtcTicks 消歧义；补 ModifiedTime 回归断言。快照二进制格式不变。
  - **Windows 管道并发挂死根因修复（f2c7b81）**：分步计时器插桩定位到 Subscribe 用例步骤 3（OnFileEvent 推送）；诊断用例云端复现实锤——同实例 pending 读 + 并发写在 Windows 挂死（Linux 不复现）；双包装方案（同一 SafePipeHandle 两个单向包装）实测不可行（句柄仅可绑定一次线程池）。终案：新增 PipeSession 把全部管道写串行化到连接泵，事件帧入队+唤醒（取消 pending 读），生产 PipeServer 与测试泵同改；空闲等待可取消、请求体不可取消保证帧完整。云端 Kernel 55 + Service 30 全绿，壳层编译步同步绿。
  - xUnit v2 不调 IAsyncDisposable：测试改 IDisposable，修管道/ContentIndexer 逐测试泄漏。
- **批次② 壳层：收口**（分身已失联，主班接手核对）：壳层代码已在 main，云端「编译壳层」步绿（0 error）；PipeClient 纯请求-响应（信号量串行化），无客户端侧并发挂死风险。遗留提示：壳层后续接事件订阅时，客户端同样要套 PipeSession 串行化模式。
- **批次③ 安装器：收口**（分身已失联，主班接手核对）：EnsureRuntime.ps1 已按全系安装器新标准落 main（不内置运行时、注册表+统一目录检测、官方源下载带进度、无网通俗重试指引、云测钩子 TAOSEARCH_FORCE_RUNTIME_DOWNLOAD）；pack-msix.ps1 已引用。m1-pack 流水线历史绿（ed86a87）。
- **出包**：版本 0.1.0.1→0.1.0.2（只增不重），pack/msix-0.1.0.2 分支触发 m1-pack（run 44879889291）。
