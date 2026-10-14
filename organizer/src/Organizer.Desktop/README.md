# 桌面工作台

原生 Avalonia / MVVM 界面，仅提供只读扫描、离线候选匹配、人工审核、SQLite 索引和整理计划预览。没有改名、移动、覆盖或自动执行入口。

## 启动

从 `organizer` 目录运行：

```sh
export AVALONIA_TELEMETRY_OPTOUT=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet run --project src/Organizer.Desktop
```

PowerShell 对应设置：`$env:AVALONIA_TELEMETRY_OPTOUT='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'`。验证脚本已设置两项；项目会在未禁用 Avalonia 构建遥测时拒绝编译。

需要 .NET 10 和 Python 3.12。可通过侧栏的“查看完整路径”输入源目录，也可用“更换文件夹”打开系统选择器。

- `--python /path/to/python3`：指定 Python 可执行文件。
- `--worker /path/to/worker/main.py`：指定工作进程。默认从程序目录及当前目录向上查找 `worker/main.py` 或 `organizer/worker/main.py`。
- `--demo /path/to/synthetic-media`：启动后自动扫描指定目录。不会创建样例或改动该目录。
- `--smoke-test-ui`：使用临时的虚构媒体执行 MVVM 交互检查并退出。
- `--smoke-test-view --demo /path/to/synthetic-media`：通过真实 Avalonia 控件执行 Headless 指针/键盘检查。建议运行 `python scripts/check_view.py dotnet` 自动生成、清理临时样例。
- `--demo /path/to/synthetic-media --screenshot /tmp/workbench.png`：使用 Avalonia Headless + Skia 渲染并退出。默认只扫描，不接受资料。
- `--screenshot-fixture`：仅当目录由 `scripts/create_demo.py` 生成、专用标记与所有媒体字节都符合中性样例时，才演示匹配和单项审核。任意真实媒体目录不会自动接受资料。
- `--screenshot-plan`：显示仅已审核文件的整理预览。
- `--screenshot-empty`：展示未选文件夹的空状态。
- `--screenshot-width 1200 --screenshot-height 800 --font-scale 1.25`：验证较小窗口和较大字号。默认 1440×900，字体比例支持 0.85–1.5。

截图与实际控件检查使用新的临时 SQLite 索引，截图只写入明确指定的 PNG 路径；不会改动媒体目录或原有审核记录。正常启动不会自动接受候选。海报由本地矢量控件绘制，仅在确有离线样例候选时显示相应抽象示意图；未知文件显示中性媒体图标，不下载图片。

## 工作方式

- 点击一行查看资料；复选框独立选择批处理队列。过滤后隐藏的勾选仍保留，“清除”会清除整个队列，表头复选框只作用于当前可见文件。
- “识别所选”最多每批 50 项，按顺序读取离线候选，不批量接受资料。超过上限会先提示，避免只处理一部分造成误解。
- “逐项审核”固定当前勾选队列；“确认并下一项”只确认当前一项，保存失败不会跳过。多个候选须先明确选择，或主动填写人工资料。队列外的焦点不能悄悄加入审核队列。
- 未确认条目显示“命名草案”。左侧“整理预览”只包含已审核项；两者都调用相同的路径与冲突检查，但草案不写入正式计划。
- “检索方式与候选”可切换标题/番号和来源。xcity 番号显示命名空间；TPDB slug 显示为来源站内 ID，不能冒充影片番号。检索条件变化立即撤销旧候选选择。
- 字段修改会撤销原审核并使旧计划失效；“撤销未确认修改”只在媒体与伴随文件仍匹配快照时恢复。重新识别不会覆盖已锁定的好资料。
- 来源、目标和原文件完整路径可查看、选择和复制；伴随字幕与其媒体一起展示。日志默认折叠，任务仍保留明确状态和取消入口。

## 快捷键

| 快捷键 | 行为 |
| --- | --- |
| Ctrl / Cmd + O | 选择源目录 |
| F5 | 只读扫描 |
| Esc | 取消当前任务 |
| Ctrl / Cmd + F | 聚焦文件筛选 |
| Ctrl / Cmd + Enter | 确认当前资料并进入下一项 |
| Ctrl / Cmd + P | 显示并生成预览计划 |

媒体列表、候选列表与输入框支持系统标准 Tab 和方向键导航。确认按钮禁用时不会借快捷键绕过校验。

## 状态与安全

- 未审核的文件不会加入计划。修改已确认的字段会使审核状态与旧计划失效。
- 显示“标识完全一致”或“标题候选”等证据类别，不将启发式评分当作概率。每个候选都需要明确的人工确认。
- 来源与审核原因在候选检查器中显示；已确认资料保留来源和人工锁定标记。
- 索引默认保存在系统 LocalApplicationData 下的 `MDCxOrganizer/index.db`。审核快照包含媒体和伴随文件的路径/大小/修改时间变化信息，不是内容哈希或重复文件证明。扫描后只恢复快照匹配的审核。
- 此前预览版没有媒体组快照的审核会明确提示重新核对；旧记录保留，不进行破坏性迁移。编辑撤销的审核不会在重新扫描或重启后自动复活。
- 持久化失败会显示日志和状态，当前会话仍可继续。运行、完成、取消和失败的任务写入 SQLite。
- 切换源、切换条目、取消与重复点击都受到取消令牌、请求代次与忙碌状态保护。旧匹配结果不能覆盖新的选择。
- 候选仍使用手工虚构的历史契约夹具，没有连接实时站点。

## 界面依赖

Avalonia / Desktop / Themes.Fluent / Headless 11.3.22，官方 MIT DataGrid 11.3.13。未使用 Accelerate、商业 TreeDataGrid、React 或嵌入式 WebView。

版本来源：
- https://www.nuget.org/packages/Avalonia/11.3.22
- https://www.nuget.org/packages/Avalonia.Controls.DataGrid/11.3.13

## 已验证与限制

自动化覆盖 ViewModel 工作流、取消、重复执行、过期异步结果、勾选/焦点分离、顺序审核、候选来源、编辑与撤销、索引恢复和源文件不变性；还通过真实 Avalonia Headless 控件验证指针、复选框、键盘过滤和导航。真实 XAML 在常规、空状态、小窗口和较大字号下渲染检查。系统文件夹对话框、剪贴板和真实 Windows/macOS 桌面交互仍需相应平台验收，Headless 检查不替代这些验证。
