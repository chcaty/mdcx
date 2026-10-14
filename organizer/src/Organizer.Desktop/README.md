# 桌面工作台

原生 Avalonia / MVVM 界面，仅提供只读扫描、离线候选匹配、人工审核、SQLite 索引和整理计划预览。没有改名、移动、覆盖或自动执行入口。

## 启动

从 `organizer` 目录运行：

```sh
dotnet run --project src/Organizer.Desktop
```

需要 .NET 10 和 Python 3.12。可以直接在路径框输入本地源目录，也可以使用系统文件夹选择器。

- `--python /path/to/python3`：指定 Python 可执行文件。
- `--worker /path/to/worker/main.py`：指定工作进程。默认从程序目录及当前目录向上查找 `worker/main.py` 或 `organizer/worker/main.py`。
- `--demo /path/to/synthetic-media`：启动后自动扫描指定目录。不会创建样例或改动该目录。
- `--smoke-test-ui`：使用临时的虚构媒体执行 MVVM 交互检查并退出。
- `--demo /path/to/synthetic-media --screenshot /tmp/workbench.png`：使用 Avalonia Headless + Skia 渲染界面并退出，无需显示服务器。若发现 DEMO-001，会演示样例匹配、人工接受和计划预览；这只在截图模式发生。
- 在截图命令后增加 `--screenshot-plan` 可显示整理计划页签。

截图模式会写入明确指定的 PNG 文件与应用自己的 SQLite 索引；不会写入媒体目录。正常启动不会自动接受候选。

## 快捷键

| 快捷键 | 行为 |
| --- | --- |
| Ctrl / Cmd + O | 选择源目录 |
| F5 | 只读扫描 |
| Esc | 取消当前任务 |
| Ctrl / Cmd + F | 聚焦文件筛选 |
| Ctrl / Cmd + Enter | 确认当前人工审核 |
| Ctrl / Cmd + P | 显示并生成预览计划 |

表格、页签、候选列表与输入框均支持系统标准 Tab 和方向键导航。

## 状态与安全

- 未审核的文件不会加入计划。修改已确认的字段会使审核状态与旧计划失效。
- 显示“标识完全一致”或“标题候选”等证据类别，不将启发式评分当作概率。每个候选都需要明确的人工确认。
- 来源与审核原因在候选检查器中显示；已确认资料保留来源和人工锁定标记。
- 索引默认保存在系统 LocalApplicationData 下的 `MDCxOrganizer/index.db`，不会在所选媒体目录创建数据库。扫描后只恢复指纹仍匹配的已审核资料。
- 持久化失败会显示日志和状态，当前会话仍可继续。运行、完成、取消和失败的任务写入 SQLite。
- 切换源、切换条目、取消与重复点击都受到取消令牌、请求代次与忙碌状态保护。旧匹配结果不能覆盖新的选择。
- 候选使用虚构离线 fixture；未连接实时站点。海报为本地占位图，不会请求远程图片。

## 界面依赖

Avalonia / Desktop / Themes.Fluent / Headless 11.3.22，官方 MIT DataGrid 11.3.13。未使用 Accelerate、商业 TreeDataGrid、React 或嵌入式 WebView。

版本来源：
- https://www.nuget.org/packages/Avalonia/11.3.22
- https://www.nuget.org/packages/Avalonia.Controls.DataGrid/11.3.13

## 已验证与限制

自动化覆盖 ViewModel 工作流、取消、重复执行、过期异步结果、索引恢复、审核与预览失效、源文件不变性。Headless 渲染用于检查真实 XAML 布局。系统文件夹对话框与真实 Windows/macOS 桌面键盘交互仍需相应平台上的人工验证。
