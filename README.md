# Device Radar

免费、开源的 Windows 外设识别与改名工具。免安装，可离线使用，支持中英文。

## 使用

1. 解压便携包到可写文件夹，运行 `DeviceRadar.exe`。
2. 找到设备，选择“改名”，逐项填写名称，预览后确认写入。
3. 需要恢复时，打开“历史/恢复”，选择之前保存的版本。

`F2` 切换语言，`F5` 刷新设备，`Esc` 返回或退出。支持鼠标、方向键和 `Tab`，窗口至少为 80×24。

需要管理员权限时，从主界面选择“管理员重开”。备份或移动程序时，一起保留旁边的 `data` 文件夹，其中保存着恢复记录。

## 支持范围

当前在 Windows 11 x64 上验证，Windows 10 22H2 待实测。公开包默认只读，系统改名需匹配本机验证配置；可修改的位置会在界面标明，详见[支持矩阵](docs/SUPPORT.md)。

## 开发

安装 .NET 10 SDK 后，在项目根目录运行：

```powershell
.\run.ps1
```

生成自包含便携包：

```powershell
.\build.ps1 -Action publish
```

[构建说明](docs/BUILD.md) · [验收记录](docs/ACCEPTANCE.md) · [MIT 许可证](LICENSE) · [第三方声明](THIRD-PARTY-NOTICES.md)
