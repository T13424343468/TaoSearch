# 扮演验收看后记录（六.10：主班亲看后填写）

- 场次：run 45227036776（修复头 74c6443：热键委托静态字段保活 + 扮演脚本 P3/D3 整改）
- 结论：**全绿定谳，CRASH 0 项**。账面 43 条观察全部核对，eventlog 无异常关键字，hotkey 注册全程 result=True。

## 逐项亲看（六.10 留痕）

| 证据 | 亲看结论 |
| --- | --- |
| RP-P3-quick.png | 「TaoSearch 快速搜索」窗叠在主窗上唤起，底部提示「Alt+Q 唤起 / 隐藏 · Esc 隐藏窗口」——上轮 FailFast 崩溃修复后快速窗正常唤起 |
| RP-P3b-quick-search.png | 快速窗键入 readme 即出命中 readme.txt（全路径 C:\taosearch-rp-corpus\docs\readme.txt），输入即搜生效 |
| RP-D2b-onboarding-again.png | 坏设置→默认值→引导窗重现（「欢迎使用 TaoSearch」第一步），观察项证据在案；脚本先走完引导再继续，怪查询不再打进引导窗 |
| RP-D3-weird-queries.png | 主窗空查询态「准备好了·索引已就绪·输入即搜」，8 条怪查询全在主窗执行、命中 0、无崩溃 |
| RP-N1-onboarding.png | 干净环境首启引导正常出现 |

## 本场修复闭环

1. **产品缺陷（真崩溃）**：GlobalHotkeyService WndProc 委托被 GC 回收，Alt+Q 消息到达即 FailFast（0x80131623）。上一场 run 45223992314 的 RP-P3 红灯即此缺陷实锤（eventlog FailFast 栈帧 DispatchMessage）。修法：委托提升静态字段终身 rooted（commit 74c6443）。本场 Alt+Q 正常，缺陷闭环。
2. **脚本误判两处**：P3 快速窗检测改 10 秒轮询+UIA 全树降级；D3 坏设置重走引导先收尾再测、命中 -1 记观察不计命中（commit 5c503e4）。本场两处均按预期工作。

## 遗留观察项（不判红，报老板知悉）

- 坏设置→默认值→重走引导：设置文件损坏后应用走默认值并重现首启引导。行为本身可辩护（设置不可读即视为首装），但「用户索引设置是否一并重置」属产品决策，收口时随对账表呈报。
