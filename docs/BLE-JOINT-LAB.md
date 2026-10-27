# VGN 键盘联合改名实验

本页为 2026-10-27 联合实验历史；其残留在 2026-10-28 经 [限定运行时恢复](BLE-CURRENT-RECOVERY.md)收尾，正常重连后的独立读取与真实列表均为原名。下述检查点及重启建议不代表本轮仍有残留，继续操作须先读取当前状态。

最新进度：已在不重启电脑的情况下完成[根因调查](BLE-ROOT-CAUSE.md)。真实重连的 ETW 栈确认微软 BLE 枚举驱动根据运行时名称回写 PnP；BTHPORT 内存别名未随直接注册表恢复而同步。运行时同步修复尚未执行，后续不把“再重启一次”当作已确认的恢复方法。

2026-10-27，Windows 11 x64 build 22631。目标为 VGN N75 PRO-1。复现 [BluFang 的字段组合](https://github.com/VAROIndustries/BluFang/blob/83ce7d0aab607fc0fad723a1ca518a95d755be6e/BluFang.ps1)：先写该设备 BTHPORT 缓存的 FriendlyName，再写同一 BTHLE 根节点的 PnP FriendlyName。使用现有受守卫探针；没有运行第三方脚本，没有改变注册表权限。

## 当前结果

| 阶段 | 缓存 FriendlyName | PnP FriendlyName | Windows 蓝牙列表 |
| --- | --- | --- | --- |
| 写前 | REG_BINARY 单字节 `00` | VGN N75 PRO-1 | 待本轮界面核对 |
| 联合写入并退出 | Device Radar 测试键盘 | Device Radar 测试键盘 | 用户确认显示测试名 |
| 键盘同配对重连 | Device Radar 测试键盘 | VGN N75 PRO-1，已被系统覆盖 | 用户确认恢复 VGN N75 PRO-1 |
| 电脑重启 | Device Radar 测试键盘 | 重新变为 Device Radar 测试键盘 | 用户确认显示测试名 |
| 重启后再重连键盘 | Device Radar 测试键盘 | Device Radar 测试键盘 | 用户确认仍显示测试名 |
| 工具立即恢复原值并退出 | REG_BINARY 单字节 `00` | VGN N75 PRO-1 | 用户确认显示原名 |
| 恢复后再重连键盘 | REG_BINARY 单字节 `00` | 又变为 Device Radar 测试键盘 | 用户确认又显示测试名 |
| 恢复后电脑重启 | 待验证 | 待验证 | 用户选择稍后重启 |

04:41:18–04:41:19 UTC 两处 API 写入均成功，各自独立回读为 proposed。04:41:36 UTC 新进程及 Get-PnpDeviceProperty 再次确认两处新名；Name、LEName 仍为工厂名，DeviceDesc 仍为 Bluetooth LE Device，设备状态 OK，探针进程数为零。

写前系统启动时间为 `2026-10-26T08:49:44.5000000Z`。重连和重启后仅运行只读观察，不重新 apply，以免掩盖系统覆盖。后续按实际界面、原始字段和系统解析名称分别记录。

用户确认重连前蓝牙列表显示测试名，并已关闭再开启键盘、保持原配对。04:47:04 UTC，只读观察确认完整身份匹配，缓存 FriendlyName 仍为测试名，但 PnP FriendlyName 和 DEVPKEY_NAME 均已回到 `VGN N75 PRO-1`；Name、LEName 与 DeviceDesc 未变，设备状态 OK，探针进程数为零。未执行恢复或重写。用户随后确认实际蓝牙列表也恢复 `VGN N75 PRO-1`。联合方案未通过当前键盘的重连持久性验收。

用户随后手动重启 Windows 并回复“已重启”。系统启动时间已变为 `2026-10-27T08:09:03.5000000Z`。08:15:19 UTC 的 after-reboot 观察确认：缓存 FriendlyName 保持测试名，同一实例的 PnP FriendlyName 和 DEVPKEY_NAME 也重新变为测试名；Name、LEName 仍是原始工厂名，DeviceDesc 未变，设备状态 OK，探针进程数为零。

本轮没有重新 apply，实验包只包含最初两次 apply 的 intent 和 completed，四条事件的 seal 均已核验。用户确认重启后实际蓝牙列表也显示测试名。该变化提示电脑启动可能重新采用了缓存别名，具体刷新时点与写入方尚未确定。

用户随后再次关闭再开启键盘并确认蓝牙重连。08:19:39 UTC，after-reboot-reconnect 只读观察确认：完整身份仍匹配，缓存 FriendlyName、PnP FriendlyName 和 DEVPKEY_NAME 均保持测试名，Name、LEName 和 DeviceDesc 均保持原值，设备状态 OK，探针进程数为零，电脑启动时间未再次改变。用户确认实际蓝牙列表仍显示测试名。当前 VGN、build 22631 的“联合写入 → 退出 → 电脑重启 → 一次同配对重连”流程通过；没有重新 apply 或常驻维持程序。

已观察到的行为分为两个阶段：首次写入后直接重连会恢复原名；电脑重启后测试名重现，此后一次正常重连的程序回读与实际列表均保留。产品流程应将该组合记为“包含一次电脑重启的本机实验路径”，不能把初次写入后的回读当作持久生效。尚未单独验证只写缓存加重启是否已足够，也未验证用蓝牙栈刷新代替电脑重启。

## 原名恢复与当前检查点

用户选择恢复原名。08:23:58–08:24:00 UTC，`Restore-BleJoint.ps1` 使用最初两个不可变恢复点，按 PnP → 缓存逆序调用既有受守卫 restore；两处 API 成功并独立回读 original。08:24:11 UTC，新进程与独立 PnP 属性核对：缓存别名为原始单字节 `00`，PnP FriendlyName 和 DEVPKEY_NAME 均为 `VGN N75 PRO-1`，其余控制字段不变，设备状态 OK，探针进程数零。用户确认实际列表显示原名。

用户随后再次关闭再开启键盘。08:25:52 UTC，只读观察发现缓存别名仍为 `00`，Name/LEName 均为工厂名，但同一实例的 PnP FriendlyName 和 DEVPKEY_NAME 又变为测试名。用户确认实际列表也再次显示测试名。没有重新 apply 或再次 restore。该结果支持“磁盘别名与蓝牙栈运行时名称状态分离”的判断，尚未定位具体读取者/写入者。

原名恢复的即时写入、回读和界面通过，恢复后的重连持久性未通过；不能记为完整恢复。用户已选择“稍后重启，先保留当前状态”。当前保持缓存原值、PnP/实际列表测试名，等待下次电脑重启后只读核验。恢复前后的电脑启动时间仍为 `2026-10-27T08:09:03.5000000Z`，后续先确认启动时间变化，再执行 after-restore-reboot，核对四字段与实际列表；不重新 apply 或 restore 掩盖变化。

用户随后要求不重启电脑调查根因。16:45 的第一轮取证先受守卫恢复 PnP 原名，等待重连超时后正常停止，正对照写入栈完整。16:58 的第二轮再次受守卫恢复 PnP，用户确认重连后于跟踪相对 17.317174 秒捕获 System/LEEnumerator 写回，磁盘别名仍为原始 00；没有电脑、服务或适配器重启。两轮跟踪均正常停止且丢失事件为零。当前字段状态仍是缓存原值、PnP 测试名；未调用新的运行时别名更新接口。完整代码链、取证与后续修复边界见[根因记录](BLE-ROOT-CAUSE.md)。

## 本机实验包与恢复

目录：`artifacts/device-lab/ble-joint-20261027/`，被版本控制忽略。程序为既有 v3 自包含包的独立副本，SHA-256 `75aacf00cfa10f4163ce71432bfb39ffab636506fc4619dadaf229539701c303`，60 项纯检查通过。

- 四字段原始快照：`c5c67991185e4667980c9e72b59970da.ble-snapshot.json`。
- 缓存恢复点：`7ade42d927654b518fbafca4c39b6c6b.json`。
- PnP 恢复点：`857f46c9405c4ba1b7a290e2f26fa9c8.json`。
- 三份原始文件的 seal 已核验；完整 Target 身份一致。原始实例、地址与容器仅留在本机 data 中。
- `Apply-BleJoint.ps1` 固定程序散列、两个 record token、目标短标识；按 JSON 回读判断成功。两条 apply 成功时设计退出码仍为 1，表示界面尚未核验。
- `apply-session.json` 保存实际写入顺序和结果。`Observe-BleJoint.ps1` 保存每阶段只读证据，含系统启动时间和进程数。
- `restore-session.json` 保存两处逆序恢复的 API 与独立回读结果；恢复后的重连单独保存在 after-restore-reconnect 观察中。

继续观察：

```powershell
& .\artifacts\device-lab\ble-joint-20261027\Observe-BleJoint.ps1 -Stage after-reconnect
# 用户手动重启电脑并返回后：
& .\artifacts\device-lab\ble-joint-20261027\Observe-BleJoint.ps1 -Stage after-reboot
& .\artifacts\device-lab\ble-joint-20261027\Observe-BleJoint.ps1 -Stage after-reboot-reconnect
# 当前待办：用户下次重启后核验恢复，不重新写入：
& .\artifacts\device-lab\ble-joint-20261027\Observe-BleJoint.ps1 -Stage after-restore-reboot
```

原值已按 PnP → 缓存逆序恢复，当前无需重复执行。后续如确需恢复，只在原值或本次拟写值符合守卫时操作；系统已回原值则 NoOp，第三方值或身份变化停止。两个写入不是原子事务，任一步失败须保留实际部分状态及恢复记录。
