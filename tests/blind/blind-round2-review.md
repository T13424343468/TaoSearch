# 盲测第二轮证据亲看记录（六.10 看后留痕，2026-11-24）

- 班次：blind-test run 45313518283（completed success，26m43s，main@1fab25b，镜像 30 万文件）
- 亲看范围：关键截图 4 张逐张看过 + 录屏 mp4 抽帧 4 帧亲看（30s/150s/300s/420s）+ findings.md + eventlog-taosearch.txt + collected-hotkey.log
- 定谳：**真绿，零自动发现**。findings.md 自动探得线索为空（新弹窗检测全程零命中）；eventlog 0 处 System Error / 0xc0000005 / FailFast；hotkey.log 两次热键注册 result=True。三路画像全程推进，上轮假绿根因（窗口定位错主窗）整改后不再复发。

## 逐屏亲看（六.10 留痕）

| 证据 | 亲看结论 |
|---|---|
| BL-xiaobai-02-done-True.png | 引导走完落主窗，空白正常无弹窗——上轮「引导推进不了」假绿路径已闭环 |
| BL-shulian-04-hotkey-quick.png | Alt+Q 快速窗正常唤起，索引就绪 710,213 文件，regex 查询「未找到」空态正常 |
| BL-pohuai-05-corrupt-settings-restart.png | 破坏设置后重启：正常回落到首次使用引导窗（「欢迎使用 TaoSearch」第一步），无崩溃无报错弹窗——坏设置容错路径符合预期 |
| BL-pohuai-01-fuzz-input.png | fuzz 输入 con.txt 打进主窗：文件名/内容双标签正常，「未找到与「con.txt」匹配的结果」空态正常，索引就绪 710,213 文件，无崩溃 |
| 录屏抽帧 30s | 引导窗第一步正常（2:55 AM） |
| 录屏抽帧 150s | 建索引中 340,465 文件，MFT 直读（NTFS 主文件表），进度正常推进 |
| 录屏抽帧 300s | 建索引中 575,424 文件，持续增长无停滞 |
| 录屏抽帧 420s | 主窗 + 快速搜索窗叠开，con.txt 空态正常（3:01 AM）——录屏 443 秒完整可播（上轮 moov atom 损坏已修） |

## 本轮修复闭环

1. **产品缺陷（真崩溃）**：NameIndex 并发竞态致服务原生 AV 0xc0000005（MFT 首建线程 / USN 事件泵 / IPC 查询并发无锁，扩容 use-after-free）。修法：读写锁全程护体 + 超限截断（commit 3e2fc3d）。本轮盲测期间 ffmpeg 持续写录屏文件持续供 USN 事件（正是上轮互撞路径），26 分钟镜像 30 万文件全程零崩溃——缺陷闭环。
2. **脚本假绿口子**：每取证点 + 收尾终扫 #32770 系统错误弹窗，命中记 B-x99 置红 exit 1（commit 1fab25b）。本轮全程零命中，断言按预期工作。

## 证据缺口备注（不判红，报老板知悉）

- 本轮证据包仅有 collected-hotkey.log，缺 collected-service-crash.log / svc-*.log（与首轮同缺口）：服务日志采集口径（落 %LOCALAPPDATA%\TaoSearch 还是 stdout 重定向）待统一，下轮补齐采集项。eventlog 已覆盖崩溃事件兜底（0 异常）。
