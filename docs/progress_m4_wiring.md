# M4 引擎接线断点档案（主班亲写）

## 令
老板 2026-11-21 13:17「全部做完，所有批次」。范围=M4 服务化续做（引擎接线+壳层客户端）+ 安装器新标准落地。派工失败（depth_limit_exceeded），按降级链主班自查补位，三路串行+后台长活并发。

## 进度（2026-11-21 13:2x）
- ✅ 路1 引擎接线：ServiceKernel 8 方法真实现（搜索双路合并/状态/重建/暂停恢复/订阅 Channel 分发/解析器注册表）+ 全文索引泵（文本类扩展名低优先级后台）+ 预览文档重扫。提交 2bfe741。
- ✅ 路2 壳层：src/TaoSearch.App WinUI3 工程（管道客户端按冻结契约/主窗防抖搜索/结果高亮预览/设置页/瓷釉令牌/转换器）。提交 2bfe741。CI 加壳层编译步。
- ✅ 路3 安装器新标准：scripts/EnsureRuntime.ps1（检测/下载/统一目录/无网指引，参照 TaoZip 1032c44）+ pack-msix.ps1 挂载。提交见 git log。
- ✅ 沙箱验证：56/56 全绿（编译零错零警）；EnsureRuntime.ps1 语法干跑 SYNTAX-OK。
- ⏳ 云端 CI：windows-ci 44870550551（壳层编译首验）/ 44870578123（后续提交）跑中；benchmark.yml 解析错已修（分支通配符引号写法）。
- 待办：CI 全绿后更新档案与交付汇报；壳层 XAML 云端编译红灯按四件套处置。

## 现状对账（2026-11-21 13:2x）
- 主线 main @ 2bfe741+：M4 契约冻结 + 云真机侧载工作流 + 基准班产物 + 引擎接线 + 壳层 + 安装器新标准。
- 服务宿主 Program.cs：8 方法真分发（并行班补齐，与主班 SearchService.cs 并存——SearchService 为组合实现备选，ServiceKernel 为主用；收口时二选一删冗余）。

## 验收口径
- 全量编译零错零警 + 全测全绿（沙箱可跑部分）+ 云端 Windows CI success。
- 壳层/安装器云端 run 号回填本档案。

## 制度对照三行
1. 已通读：tao-result-first 全文、tao-subagent-dispatch 全文、tao-line-watch 速览+一二三四章、tao-delivery-gate 全文、tao-knowledge-hub 速览。
2. 调度制度 §一20 派工失败降级链改变了本班做法：分身派不出即主班自查补位，三路串行+后台并发，不原地硬试。
3. 回灌：无新坑（depth_limit_exceeded 为平台层级限制，非流程缺陷）。
