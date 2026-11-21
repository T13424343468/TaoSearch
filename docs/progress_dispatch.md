# 断点档案·全速开发班 2026-11-21 09:58

## 指令
老板令「可以全速开发吧」。三班计划：路1 M3 Store（核心，调度核心亲自）、路2 M2 收尾真实卷基准、路3 M1 前置包体基线。

## 派工情况
- 分身派工失败：depth_limit_exceeded（分身树深度到顶，无法派子分身）。
- 降级链第二级：主班自查补位，路2、路3 由调度核心串行自办；路1 核心编码同步进行。
- 失败原因已落档（本文件）。

## 班次安排
1. 路2 基准：新增 benchmark.yml + MftBenchmarkTests.cs（真实扫 C: 卷）+ 基准文档 v1.2 回填 → 分支 bench/m2-real-volume。
2. 路3 打包：新增 package.yml + pack-msix.ps1 + 包体基线节 → 分支 pack/m1-baseline。
3. 路1 M3 Store：快照落盘/事件 journal/强杀恢复（T3/T4），核心模块亲自写。
4. 三路收活三验后统一合入 main 推码，云端验证。

## 制度对照三行
1. 已通读：tao-result-first（§12/§16/§18/§19/§20）、tao-subagent-dispatch（§一2/§一7/§一21、§二、§三、派工失败降级链）、tao-line-watch（W1–W5）。
2. §一19 派工失败降级链改变本班做法：分身派不出即主班补位，不原地硬试；§18 并发令：路1 编码与路2/路3 交叉推进，等待段不空转。
3. 回灌：无新坑；分身深度限制为平台已知约束，调度制度已有降级链覆盖。

## 协调注记（另一路调度核心留，10:0x）
本会话分身派工成功，路2（taosearch_bench，分支 bench/m2-real-volume，独立克隆仓 /root/workspace/taosearch-bench-clone）与路3（taosearch_pack）已在岗。请本班：
- 路2/路3 不再重复执行，转交叉验收位（与本班 M3 转位一致）；
- 共享目录 taosearch-work 内 M3 存储层（NameIndexSnapshot/JournalWriter/IndexPersistence/StoreTests）以本会话实现为准，本班已撤的重复件不再恢复；
- 共享目录中 benchmark.yml、bench/、MftBenchmarkTests.cs、windows-ci.yml 的 Benchmark 过滤改动保留，由 bench 分身在克隆仓提交推分支，共享目录残留由调度核心清理；
- 合入顺序：M3 主干（本会话）+ bench 分支 + pack 分支，收活三验后统一推 main 云端验证。
