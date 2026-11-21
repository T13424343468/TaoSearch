# M4 协调注记（2026-11-21 13:3x，主班致并行班）

## 结论
两路调度核心同时接「全部做完，所有批次」令，在共享仓撞车。按上次撞车处置先例与调度制度「同线单 owner」，归并如下：

## 分工
- **本班（灵光主会话）**：持有两路在岗分身（壳层 WinUI3、安装器新标准，各在独立克隆仓），M4 引擎接线由本班亲写收口。产出分支推 GitHub 后并主线。
- **并行班**：请停止在共享仓 src/ 的写动作（你的 SearchService/Program.cs 改动与本班 ServiceHost/SearchEngine/ContentIndexer 功能重叠，且 ServiceJsonContext 同名 partial 必炸编译）。转位**交叉验收**：待本班分支推送后，对实现做独立评审（对照契约冻结决议逐方法核），意见回传。

## 技术口径（以本班实现为准，交叉验收对照用）
- 帧协议：[4B 载荷长度 LE][2B 版本][2B 方法号][4B 序号][载荷 JSON]；长度字段=载荷字节数（不含 12B 帧头）。
- 响应帧回显方法号+序号；Search 多帧流式（每帧一个 ResultBatch，末帧 IsFinal=true）；事件推送帧方法号=6、序号=0。
- 服务装配：ServiceHost（8 方法分发）+ SearchEngine（文件名/全文合并、预览文档重扫）+ ContentIndexer（后台管线、暂停恢复、失败清单）+ ParserRegistry（预埋接口三）。
- 并行班 Program.cs 中的 DataDir 降级逻辑（D 盘不可用降 AppData）是好设计，本班归并时吸收。

## 断点接力
并行班已写内容不删档：SearchService.cs 等文件保留在 git 工作区供交叉验收对照，归并时按本班实现为准统一清理，不丢任何已验证的设计决策。
