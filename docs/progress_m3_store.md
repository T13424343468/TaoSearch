# 进展断点 · M3 Store 班（2026-11-21 09:58 老板令「可以全速开发吧」）

## ⚠️ 对撞协调（10:0x）
发现**另一路调度核心**（老板在并行会话下达了同一全速令）在同一工作区执行完全相同的三路计划，且已铺开：引擎接线（IJournalSink）、快照序列化（NameIndexSnapshot，partial+CRC+删除压缩）、基准（benchmark.yml + MftBenchmarkTests + bench/）、打包（进行中）。其断点档案：docs/progress_dispatch.md。

按「同线单 owner」与防对撞原则，本班处置：
- **撤下本班重复实现**：SnapshotStore.cs、EventJournal.cs、StoreTests.cs（与对方 NameIndexSnapshot/JournalWriter 方案重复；对方方案集成更深——CRC 校验、删除记录压缩、恢复专用构造预分配）。
- **保留对方已落改动**：CoreContracts（IJournalSink）、FsIndexEngine（journal 挂点+恢复构造）、NameIndex（partial+本班 EnumerateLive 共存）、windows-ci.yml（Benchmark 过滤）。
- **本班转位**：交叉验收位——对方批次落定后跑统一验证（编译零错零警+全测绿）、收活三验、分支合入核对；对方卡壳时按断点接力补位。

## 本班先前产出（已撤，留档备查）
- NameIndex.EnumerateLive()（保留在树中，对方 diff 已含）
- SnapshotStore/EventJournal/StoreTests 三件（已删；自查中修过 Replay 流回退与短读两错，经验供对方方案参考：长度前缀读完勿回退流位置；单次 Read 不保证读满须循环）

## 待办
1. 监视对方批次落定（文件不再变更）→ 统一验证
2. 收活三验：对验收标准、实地抽查证据、不合格打回
3. 路2/路3 分支（bench/m2-real-volume、pack/m1-baseline）合入核对
4. 红灯口径：对方批次若长时间带编译错误停滞，按断点接力补位修复
