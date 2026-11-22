# 关1「像不像」原型逐屏对照账（主班补位，2026-11-22）

> 对照基准：projects/2375472/TaoSearch 原型（单文件版）.html（pages-data 内嵌 6 页，已解包至 .tmp/proto_pages/）。
> 被测：src/TaoSearch.App/（MainWindow.xaml 81 行、SettingsPage.xaml 53 行、App.xaml 20 行）。
> 结论：**关1 不通过**。6 页中 2 页有实现（main 部分、settings 部分），4 页未实现或仅安装器层面覆盖。

## 差距矩阵

| 原型页 | 状态 | 差距明细 |
|---|---|---|
| cover | 不适用 | 评审入口，非产品 UI |
| main 主窗口 | **有实现，偏差大** | ①原型为「文件名/内容」双标签+筛选 chips（全部/Markdown/文档/表格/代码）+排序（相关度/名称/路径/大小/修改时间），实现为单个 TextBox+ToggleSwitch，无 chips 无排序；②原型顶部导航为「搜索/内容/设置」三项，实现仅「搜索/设置」；③原型有索引状态条（文件数/内容索引篇数/更新时间），实现仅一行 StatusText；④原型定义 9 种状态（正常/空态×2/加载/报错×2/极端×3），实现均无专门空态与报错界面；⑤结果卡片命中高亮已实现（Segments），文件名/路径/大小齐 |
| quick 快速搜索窗 | **未实现** | Alt+Q 唤起迷你窗：输入即搜、键盘上下选择、Enter 打开、Ctrl+E 打开目录、Esc 隐藏、最近搜索；实现完全没有此窗 |
| onboarding 首次引导 | **未实现** | 欢迎→索引位置（默认 D:\TaoSearch\Index，备选 AppData）与范围（整 D 盘/仅下载文档桌面/自定义）→建索引进度（MFT 直读、可暂停/取消）→完成；实现完全没有 |
| settings 设置页 | **部分实现** | 已有：索引状态卡（简版）、重建索引、暂停、存储位置（只读展示）、开机自启开关。缺：索引库位置更改、排除目录增删、内容索引后台优先级、启动时显示快速搜索窗、外观主题切换（水墨/墨白）、快捷键设置（Alt+Q、Ctrl+Shift+S）、关于+检查更新 |
| shell Win11 集成 | **部分覆盖** | 右键菜单（文件/文件夹）已由安装器勾选实现；任务栏跳转列表未做；全局快捷键未做 |

## 关1 不通过项（必须补齐才能出包）

1. quick 快速搜索窗整页（含最近搜索、键盘导航、隐藏/唤起）
2. onboarding 首次索引引导整页（含索引位置选择与范围）
3. 设置页缺项 7 项（见上表）
4. 主窗口：双标签+chips+排序、索引状态条、空态/报错界面
5. 全局快捷键 Alt+Q（quick 窗的前置）与 Ctrl+Shift+S
6. 任务栏跳转列表

## 处置

- 按用户价值排序补齐：quick 窗+全局热键 → onboarding → 设置页缺项 → 主窗口状态与 chips/排序 → 跳转列表。
- 每补一项即纳入关2 自动化用例清单，补完重跑关1 对照。

## 处置回执（2026-11-22 产品页补齐班）

| 不通过项 | 处置 | 落点 |
|---|---|---|
| quick 快速搜索窗整页 | **已补** | QuickSearchWindow.xaml/.cs：输入即搜（100ms 防抖、文件名路）、↑↓ 选择、Enter 打开、Ctrl+E 打开目录、Esc 隐藏、空态最近搜索（settings.json 持久化）、底部快捷键提示逐字对照原型 |
| onboarding 首次引导整页 | **已补** | OnboardingWindow.xaml/.cs：欢迎→索引位置（D:\TaoSearch\Index / AppData）与范围三选→建索引进度（轮询 GetIndexStatus、MFT 直读文案、暂停/继续）→完成；文案逐字对照原型 |
| 设置页缺项 7 项 | **已补 6 项** | SettingsPage：索引库位置更改（FolderPicker）/排除目录增删/内容索引优先级/启动时显示快速搜索窗/主题切换（水墨/墨白）/关于+检查更新（如实告知更新服务未接入，不伪造「已是最新」）；快捷键自定义按原型口径标注「当前版本固定」 |
| 主窗口双标签+chips+排序/索引状态条/空态报错 | **已补** | MainWindow：文件名/内容双标签（单选互斥）、chips 全部/Markdown/文档/表格/代码、排序 ComboBox 五项、索引状态条（就绪/建索引中+文件数）、空态（准备好了/未找到/内容未找到）与报错横幅 |
| 全局快捷键 Alt+Q | **已补** | GlobalHotkeyService：后台线程 message-only 窗口 RegisterHotKey，WM_HOTKEY 经 DispatcherQueue 回 UI 线程；Ctrl+Shift+S 未做（原型主入口为 Alt+Q） |
| 任务栏跳转列表 | **未做** | 留待下一班（JumpList 需 MSIX 口径核对） |

编译验证：windows-ci 561337f 全绿（WinUI3 XAML 编译含壳层，零错）。修正记录：RaiseEvent→直调、Window.Hide→AppWindow.Hide、IsAlwaysOnTop→OverlappedPresenter、CI 补 -p:Platform=x64。
