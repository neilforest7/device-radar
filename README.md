# Device Radar

Windows 外设识别与系统昵称管理工具，目前为设计与 TUI 样稿阶段。

样稿由 Terminal.Gui 2.5.0 的正式控件绘制，浅色蓝选中行、独立改名对话框、修改预览、部分完成结果及窄屏布局均使用示例数据。

另有独立原生探针，用于已确认的鼠标、BLE 键盘与 USB 麦克风的可恢复定点命名实验。

AB13X 麦克风已跑通普通权限原生改名、名称同步、声音设置刷新和恢复，提供自包含单文件实验程序；设备、系统版本与生命周期的实际验收范围见[实测记录](docs/BLUETOOTH-AUDIO-LAB.md)。

VGN 蓝牙键盘已通过驱动代码分析、重连写入跟踪和只读驱动查询定位名称覆盖来源；运行时名称同步修复尚未实现，详见[根因报告](docs/BLE-ROOT-CAUSE.md)。探针限定已实测的设备和系统版本。

- [产品设计](DESIGN.md)
- [真实设备识别与改名流程](docs/DEVICE-FLOW.md)
- [名称持久性研究与验证计划](docs/NAME-PERSISTENCE.md)
- [原生命名探针](prototypes/DeviceRadar.NativeProbe/README.md)与[当前鼠标实测记录](docs/MOUSE-PERSISTENCE-LAB.md)
- [蓝牙键盘与 USB 麦克风实测](docs/BLUETOOTH-AUDIO-LAB.md)
- [BLE 名称覆盖根因](docs/BLE-ROOT-CAUSE.md)
- [TUI 方案、运行与渲染](docs/TUI-PREVIEW.md)

安装 .NET 10 SDK 后，在项目根目录运行：

```powershell
.\run-preview.ps1
```

生成 ANSI 字符网格样稿：

```powershell
.\render-preview.ps1
```

打开浏览器查看器需要 Node.js：

```powershell
cd tools\tui-renderer
npm ci
npm start
```

查看器地址为 [http://127.0.0.1:4178](http://127.0.0.1:4178)。画面由 xterm.js 直接解释框架导出的 ANSI；交互操作在原生 TUI 样稿中进行。
