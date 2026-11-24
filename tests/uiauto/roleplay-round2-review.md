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

---

# 扮演验收看后记录·第二轮（六.10 看后留痕，2026-11-24）

- 班次：roleplay-accept run 45313534527（completed success，7m8s，main@1fab25b）
- 背景：服务 0xc0000005 根因定谳为 NameIndex 并发竞态（MFT 首建线程 / USN 事件泵 / IPC 查询并发无锁，EnsureNamesCapacity 扩容 use-after-free），修复 commit 3e2fc3d（读写锁全程护体 + 超限截断）；扮演脚本假绿口子整改 commit 1fab25b（每取证点+收尾终扫 #32770 系统错误弹窗，命中置红 exit 1）。
- 定谳：**真绿，CRASH 0**。观察 36 条、录屏帧 104、截图 19 张；弹窗检测全程零命中。

## 逐屏亲看（六.10 留痕）

| 证据 | 亲看结论 |
| --- | --- |
| RP-P3-quick.png | Alt+Q 快速窗正常唤起叠主窗，主窗 readme 命中 1 条，索引就绪 45,471 文件——上轮热键 GC 崩溃路径（RP-P3）修复后保持绿 |
| RP-D3-weird-queries.png | 怪查询后主窗空态「准备好了·索引已就绪」，无系统错误弹窗——服务 0xc0000005 并发 AV 修复后未见复发 |
| RP-N4-onboarding-done.png | 引导索引完成页 45,471 文件、MFT 直读（NTFS 主文件表），「完成」按钮就绪 |
| RP-N6-search-readme.png | readme 命中 1 条（C:\taosearch-rp-corpus\docs\readme.txt，38 B），路径完整 |

## 日志亲扫

- service.log / service.err.log / app.log：0 处 System Error / 0xc0000005 / FailFast / Exception 关键字。
- 结论：扮演班真绿，无假绿迹象。

## 证据缺口备注

- 本轮证据包仍缺 collected-service-crash.log / svc-*.log（与首轮同缺口）：服务日志采集口径（落 %LOCALAPPDATA% 还是 stdout 重定向）待统一，下轮补齐采集项。
