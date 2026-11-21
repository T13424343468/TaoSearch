# M4 引擎接线断点档案（主班亲写）

## 令
老板 2026-11-21 13:17「全部做完，所有批次」。范围=M4 服务化续做（引擎接线+壳层客户端）+ 安装器新标准落地。派工失败（depth_limit_exceeded），按降级链主班自查补位，三路串行+后台长活并发。

## 现状对账（2026-11-21 13:2x）
- 主线 main @ efed65f：M4 契约冻结 + 云真机侧载工作流 + 基准班产物已归档。
- 服务宿主 Program.cs：管道帧协议在位，方法分发是 ACK 占位，未接内核。
- 内核接口已清点：FsIndexEngine（BuildAsync/Search/Snapshot/AttachListener/SaveSnapshot/MarkRestored）、NameIndex、InvertedIndex、UsnJournalListener(IFileEventSource)、QueryParser、IndexPersistence/JournalWriter/FullTextPersistence。
- 壳层工程不存在；安装器新标准未落地（TaoZip 有参照实现）。

## 拆活（降级后）
1. 路1 主班亲写：M4 引擎接线——SearchService（组合 FsIndexEngine+FullText+QueryParser）、管道方法分发真实现、事件订阅分发、IndexStatus 汇总。核心模块不外派。
2. 路2 主班续做：壳层 WinUI3 工程（Linux 无法编译，落码+云端 CI 验证）。
3. 路3 主班续做：安装器新标准（参照 TaoZip EnsureRuntime.ps1，适配 MSIX）。

## 验收口径
- 全量编译零错零警 + 全测全绿（沙箱可跑部分）+ 云端 Windows CI success。
- 壳层/安装器云端 run 号回填本档案。

## 制度对照三行
1. 已通读：tao-result-first 全文、tao-subagent-dispatch 全文、tao-line-watch 速览+一二三四章、tao-delivery-gate 全文、tao-knowledge-hub 速览。
2. 调度制度 §一20 派工失败降级链改变了本班做法：分身派不出即主班自查补位，三路串行+后台并发，不原地硬试。
3. 回灌：无新坑（depth_limit_exceeded 为平台层级限制，非流程缺陷）。
