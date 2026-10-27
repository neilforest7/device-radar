# Device Radar 0.1.0

Windows 外设识别与系统昵称管理。MIT、免费、开源、纯便携；自包含 Windows x64 程序，不需要安装 .NET。

解压完整包到可写文件夹，运行 `DeviceRadar.exe`。Tab / Shift+Tab 切换设备列表与详情，方向键选择，Enter 确认，/ 搜索，F6 中文/English，F5 刷新，Esc 返回或退出。列表顶部按 ↑ 进入筛选、← → 选择、空格切换；详情 PgUp/PgDn 滚动。80×24 起正常交互，窄屏上下分栏。

F3 全局查找实物：断开再重连目标设备，弹窗显示设备名后按 Enter 在列表中选中；Esc 取消。F4 管理员重开，F7 系统设置。

选择设备后可搜索、筛选、查找实物、查看命名位置和具体能力。可用位置支持逐项编辑、预览、保存恢复点、写入、独立回读与历史版本恢复。修改完成后退出即可。

**首版仅对本机已验证的精确设备绑定开放系统写入。** 当前受限后端为 build 22631 的指定 Logitech 接收器鼠标 PnP 和 AB13X 录音端点。公开包不含私有验证配置和设备记录；在其他机器上显示不可用原因，仍可枚举、识别、查看详情和打开 Windows 设置。Classic、BLE、播放端点尚未通过完整验收。详见 [支持矩阵](SUPPORT.md) 与 [实际验收](ACCEPTANCE.md)。Windows 10 22H2 实测与本轮电脑重启验证待办。

PnP 写入需要管理员，可从主界面选择管理员重开；录音路径普通权限即可。声音页面缓存旧名时，单独确认“刷新声音设置”会关闭并重新打开 Windows 设置。

`data/v1/<电脑指纹>/` 保存本机设置、验证配置、不可变恢复点、意图和结果；请连同 EXE 一起备份，勿公开。恢复前选择历史版本、确认实物并检查预览；设备身份、现值或系统指纹变化时停止。原本不存在的属性恢复为不存在。

启动和枚举不联网，内置 USB/PCI ID 快照可离线识别。显式更新只下载完整公开数据库，不上传设备身份或名称记录，缓存位于工具目录。程序退出后无服务、任务或后台改名。

验证文件：`SHA256SUMS.txt`。许可证：[MIT](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md)、USB-LICENSE.txt、PCI-LICENSE.txt。

Source and reproducible build instructions: [neilforest7/device-radar](https://github.com/neilforest7/device-radar)。

English: extract to a writable folder and run DeviceRadar.exe. Tab switches panes, / searches, F6 switches language, F3 locates a device after disconnect/reconnect, F4 restarts as administrator, and F7 opens settings. Review each naming location, preview, then apply. History restoration creates a fresh recovery point. Only locally validated exact devices can be written; the public archive contains no private validation profiles. Windows 10 and reboot validation remain pending. Keep the private data folder with the executable when backing up.
