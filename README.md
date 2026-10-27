# Device Radar

免费、开源、MIT 许可的 Windows 外设识别与系统昵称管理 TUI。解压到可写文件夹，运行 `DeviceRadar.exe`；完成后退出，无驻留服务或计划任务。自包含 win-x64 程序无需安装 .NET。

首版提供真实设备清单、设备变化通知、外接/内置/虚拟/离线筛选、搜索、功能聚合、拔插查找实物、USB/PCI 公开 ID 补全、中英文、逐位置编辑、预览、不可变恢复点、独立回读和历史版本恢复。浅色背景、蓝色选中行，最低正常交互尺寸 80×24；窄屏上下分栏，缩放保留输入和选择。

**系统改名为部分支持。** 已验证的写入后端限定 Windows 11 build 22631 的指定 Logitech 接收器鼠标 PnP 与 AB13X 录音端点，并要求匹配本机验证配置的精确身份与系统/驱动指纹。发布包不含私有配置；其他机器可枚举、识别、查看能力与打开 Windows 设置。未经验证的设备、Classic 蓝牙、BLE、播放端点显示具体不可用原因，不自动尝试实验路径。详见[支持矩阵](docs/SUPPORT.md)和[本轮验收](docs/ACCEPTANCE.md)。

Windows 10 22H2 为兼容目标，尚未实测。本轮不重启电脑、蓝牙服务或适配器；历史重启证据仅适用于原设备与原后端。

使用：

1. 解压完整发布包，在可写目录运行 EXE。默认按系统语言；F2 切换中文/English。
2. 搜索或选择设备，Enter 查看全部命名位置；Tab、方向键、鼠标均可操作。F5 重新枚举。
3. 改名窗口中逐项填写，检查预览再确认。需要管理员权限的 PnP 操作，可从主界面选择“管理员重开”。录音路径使用普通权限。
4. 查看各位置的 API、回读与同步结果；需要时打开对应 Windows 设置。声音页面缓存旧名时，可单独确认“刷新声音设置”，关闭并重新打开设置。
5. “历史/恢复”列出当前连接的本机记录。确认实物，选择版本并预览后恢复；弱身份不会自动应用历史。结束后退出。

工具目录的 `data/v1/<电脑指纹>/` 保存设置、验证配置、恢复点、意图和结果；`cache/identification/` 保存显式更新的公开数据库。备份或移动工具时一起保存 `data`。原始属性是否存在、类型与字节均保留；删除记录会失去相应恢复能力。损坏或未提交的文件不会作为有效恢复点。不要同时使用其他改名工具。

启动及枚举无需联网。内置 USB/PCI 数据库支持离线；显式更新只下载固定地址的完整公开数据库。序列号、蓝牙地址、完整实例路径、电脑标识及改名记录留在本机。

开发需要 .NET 10 SDK 10.0.100 起；脚本优先使用项目 `.tools/dotnet`：

```powershell
.\build.ps1
.\run.ps1
.\run.ps1 --self-check
.\build.ps1 -Action publish
.\build.ps1 -Action package
```

发布位置：`artifacts/release/v0.1.0-win-x64/`，旁边有 ZIP 和 SHA-256 校验文件。完整命令及构建约束见[构建说明](docs/BUILD.md)。开发诊断 CLI 可用 `DeviceRadar.exe inventory`、`capabilities --device <token>`、`history --device <token>`；无参数显示真实 TUI。

[产品设计](DESIGN.md) · [设备流程](docs/DEVICE-FLOW.md) · [识别来源与许可](docs/IDENTIFICATION-SOURCES.md) · [命名研究](docs/NAMING-RESEARCH.md) · [第三方声明](THIRD-PARTY-NOTICES.md) · [可续接进度](docs/IMPLEMENTATION-PROGRESS.md)

`prototypes/DeviceRadar.Preview` 为明确隔离的示例数据开发样稿；`prototypes/DeviceRadar.NativeProbe` 保留历史定点实验。生产入口仅 `src/DeviceRadar`。本机 `artifacts/device-lab` 含未分发的原始取证、封存校验值及旧恢复点，克隆仓库不包含这些资料，旧格式继续按原探针约束使用。
