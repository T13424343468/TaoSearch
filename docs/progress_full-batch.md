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
