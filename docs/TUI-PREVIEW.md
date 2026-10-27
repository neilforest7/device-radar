# TUI 字符网格样稿

沿用选定的浅色、蓝色选中行方向。主界面、独立改名对话框、修改预览、部分完成结果和窄窗口均由正式 TUI 控件绘制。最小正常交互尺寸为 80×24；窄窗口自动上下分栏，再小则显示放大提示并保留状态。

信息采用紧凑排列，减少重复标题和空行。改名对话框在所有尺寸下都逐项展开，分别显示当前名与新名输入框。

宽屏审阅样稿为 144×24；窄屏为 80×24。已打开的对话框随窗口缩放，保留输入和勾选状态。

## 复用项目

| 项目 | 本次用途 |
| --- | --- |
| [Terminal.Gui 2.5.0](https://www.nuget.org/packages/Terminal.Gui/2.5.0) | C# 终端控件、布局、焦点、键鼠、复选框、输入框与模态对话框；框架将画面导出为 ANSI 字符网格 |
| [xterm.js 6.0.0](https://xtermjs.org/) | 解释导出的 ANSI，按终端字符单元显示文字、颜色和边框，供浏览器审阅与截图 |

应用只组织设备样例和操作流程。控件排版、Unicode 宽度、终端转义和字符显示分别交给现成框架。

[Textual](https://textual.textualize.io/guide/testing/) 也有正式控件、Pilot 测试驱动和 SVG 截图导出，适合 Python TUI。本次选择 Terminal.Gui，以复用现有 C# 技术方向中的控件代码。

Terminal.Gui 的 v2 API 与 v1 不同；以其 [v2.5.0 官方指南](https://github.com/tui-cs/Terminal.Gui/blob/v2.5.0/ai-v2-primer.md)为准。ANSI 导出采用框架的 [IDriver](https://github.com/tui-cs/Terminal.Gui/blob/v2.5.0/Terminal.Gui/Drivers/IDriver.cs) 能力。

## 正确的命名位置

摄像头示例包含三个独立目标：

- 摄像头：设备管理器中的摄像头设备节点。
- 麦克风：声音设置中的录音端点。
- 设备本体：设备管理器中的 USB 复合设备节点。

每个目标独立勾选、编辑和核验。样稿中的结果是示例数据，实际写入能力仍按 DESIGN.md 的验证计划确认。

## 运行与重新渲染

开发需要 .NET 10 SDK。运行脚本优先使用项目本地 `.tools/dotnet`，其后使用已安装的 SDK，并检查版本。依赖缓存位于 `.tools/nuget`；SDK 和缓存由 `.gitignore` 排除。

在项目根目录运行交互样稿：

```powershell
.\run-preview.ps1
```

重新生成正式控件的字符快照：

```powershell
.\render-preview.ps1
```

浏览器查看真实终端输出：

```powershell
cd tools\tui-renderer
npm ci
npm start
```

打开 [本地渲染查看器](http://127.0.0.1:4178)。查看器提供不同状态的静态终端快照；交互操作在原生 C# 样稿中进行。

ANSI、纯文本和尺寸元数据保存在 `artifacts/tui/`，包括四个宽屏状态、80×24 主界面与 80×24 改名弹窗。浏览器画面直接解释这些 ANSI；页面外的切换栏只负责选择快照。

本机生成的自包含可执行样稿位于 `artifacts/preview-app/DeviceRadar.Preview.exe`，运行时使用示例数据。

## 当前范围

本轮实现的是示例数据驱动的界面样稿。设备枚举、注册表修改、提权、断开设备和 Windows 设置跳转留在实际设备能力验证阶段。
