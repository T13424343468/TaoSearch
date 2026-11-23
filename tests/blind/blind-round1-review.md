# 盲测首轮证据亲看记录（六.10 看后留痕，2026-11-24）

- 班次：blind-test run 45217148969（completed success，22m41s，main@7d2bef3 前提交）
- 亲看范围：13/13 张截图逐张看过 + 录屏 mp4 尝试播放 + hotkey.log + eventlog-taosearch.txt
- 定谳：**本轮为假绿，盲测无效**。产品无新增缺陷证据，但三路画像全部未推进引导。

## 逐屏看后记录

| 证据 | 看后结论 |
|---|---|
| BL-xiaobai-01/03/04（3 张） | 引导窗停在第一步「欢迎使用 TaoSearch」，「下一步」未点通；所谓首搜/设置截图全是引导窗未关状态 |
| BL-shulian-01~04（4 张） | 同上，四张全部停在引导第一步；快速搜索/正则/设置/热键操作均未发生在主窗 |
| BL-pohuai-01~06（6 张） | 同上，六张全部停在引导第一步；fuzz 输入打进了引导窗；杀服务/重启/坏设置/删索引为进程级真实操作，但界面始终在引导 |
| blind-recording.mp4 | 文件损坏（moov atom 缺失，ffmpeg 被 Stop-Process -Force 强杀未收尾），不可播放——已修（record-wrapper.ps1 优雅收尾） |
| hotkey.log | 两次热键注册 result=True win32err=0，正常 |
| eventlog-taosearch.txt | 空，盲测期间无崩溃事件记录 |

## 根因（脚本缺陷，非产品缺陷）

`Get-AppWindow('TaoSearch')` 精确匹配 Name='TaoSearch' 先命中引导窗后面的**主窗**（引导窗名是「TaoSearch 首次使用引导」，精确匹配不中才走包含匹配，但主窗 Name 恰好精确等于 'TaoSearch'），在主窗里找「下一步」永远找不到 → `Complete-Onboarding` 静默 return → 整班只验证了「引导窗能弹出」。且脚本无任何失败退出路径，永远退出 0，CI 判绿。

gate2 同环境 G2-12 绿，因为 gate2 按精确名「TaoSearch 首次使用引导」定位引导窗——口径差异实锤。

## 整改（本提交）

1. `blind-run.ps1`：新增 `Get-WindowExact`，引导窗按精确名定位（对齐 gate2）；`Invoke-ButtonByName` 补真实鼠标坐标点击兜底；引导推进后加硬断言（引导窗必须关闭），失败置 `blindFail` 并 `exit 1`，禁止静默假绿。
2. `record-wrapper.ps1` + workflow：录屏改包装进程托管，停止时向 ffmpeg stdin 写 q 优雅收尾，超时才强杀，保证 mp4 完整可播。
3. 本轮 run 45217148969 的证据仅作「引导窗可弹出、无崩溃」结论使用，三画像摸索结论作废，待重跑定谳。
