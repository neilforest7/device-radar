# Device Radar 0.2.0

Windows 外设识别与系统昵称管理。MIT、免费、开源、纯便携；自包含 Windows x64 程序，不需要安装 .NET。

解压完整包到可写文件夹，运行 `DeviceRadar.exe`。Tab / Shift+Tab 切换设备列表与详情，方向键选择，Enter 确认，/ 搜索，F6 中文/English，F5 刷新，Esc 返回或退出。列表顶部按 ↑ 进入筛选、← → 选择、空格切换；详情 PgUp/PgDn 滚动。80×24 起正常交互，窄屏上下分栏。

F3 查找实物：断开再重连目标设备，单个候选可在列表中选中；多个候选时请重新定位。结果只确认当前 Windows 设备条目。F4 管理员重开，F7 系统设置。

选择设备后可查看命名位置，逐项编辑可用位置，预览并确认写入。历史版本可用于恢复。支持符合条件的 PnP 设备、播放和录音端点、Classic 蓝牙；BLE 当前只读，改名计划在下个版本提供。设备状态、身份和权限会影响具体位置的可用性。本机改名、恢复及代表性设备重连已通过，重启与跨机器仍待验，详见[支持矩阵](SUPPORT.md)和[验收记录](ACCEPTANCE.md)。

PnP 和 Classic 蓝牙写入需要管理员权限，可从界面请求管理员重开。重开后恢复设备选择和筛选，需重新预览并确认操作。Classic 名称保存后可能需要正常断开并重连设备，再核验；无需取消配对。声音页面缓存旧名时，可确认“刷新声音设置”，关闭并重新打开 Windows 设置。

`data/v1/<电脑指纹>/` 保存本机设置、恢复点、意图和结果；请连同 EXE 一起备份，勿公开。恢复前选择历史版本、确认实物并检查预览；设备身份或当前值不匹配时停止。

启动和枚举不联网，内置 USB/PCI ID 快照可离线识别。显式更新只下载完整公开数据库，不上传设备身份或名称记录，缓存位于工具目录。程序退出后无服务、任务或后台改名。

验证文件：`SHA256SUMS.txt`。许可证：[MIT](LICENSE)、[第三方声明](THIRD-PARTY-NOTICES.md)、USB-LICENSE.txt、PCI-LICENSE.txt。

Source and reproducible build instructions: [neilforest7/device-radar](https://github.com/neilforest7/device-radar)。

English: extract to a writable folder and run DeviceRadar.exe. Tab switches panes, / searches, F6 switches language, F3 locates a device after disconnect/reconnect, F4 restarts as administrator, and F7 opens settings. Review each naming location, preview, then apply. Eligible PnP devices, playback and recording endpoints, and Classic Bluetooth have rename backends; BLE rename is planned for the next version. PnP and Classic Bluetooth writes require administrator permission. After saving a Classic name, the device may need a normal disconnect and reconnect before verification; do not unpair it. Availability depends on the device and permissions. Rename, restore, and representative reconnect checks passed on the local Windows 11 machine; reboot and additional machines remain unverified. Keep the private data folder with the executable when backing up.
