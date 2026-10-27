# VGN 蓝牙名称被重连覆盖的根因

2026-10-27，当前 VGN N75 PRO-1、Windows 11 x64 build 22631。本轮没有重启电脑、蓝牙服务或适配器；用户配合关闭再开启键盘一次，捕获实际回写。

## 结论

真实取证已确认：键盘重连触发设备变化通知，微软 BLE 枚举驱动重新写入 PnP FriendlyName。匹配本机驱动的静态数据流与前后行为共同指向 BTHPORT 运行时别名未同步：直接修改注册表与 PnP 没有经过运行时别名更新器。这解释了两个保存值刚恢复时能显示原名，下一次重连又出现测试名。

这里涉及三份状态：

| 状态 | 所在位置 | 当前调查确认的职责 |
| --- | --- | --- |
| 持久别名 | BTHPORT 设备键的 FriendlyName，REG_BINARY、UTF-8 加 NUL | 加载已保存设备时读入；直接改它没有调用运行时更新器 |
| 运行时别名 | BTHPORT 的 DEVICE_INFO_BLOCK（DIB）内存字段 +0x158 | 生成 BLE 设备信息及变化通知时，非空别名优先于原始 LEName |
| 当前显示名称 | BLE 根节点 DEVPKEY_Device_FriendlyName | 微软 BLE 枚举驱动调用 IoSetDevicePropertyData 写入；本机 Windows 蓝牙列表采用该结果 |

字段职责由匹配本机二进制与微软公开符号的反汇编确认。实际 PnP 写入者、时序及栈由 ETW 捕获；磁盘与 PnP 值由独立回读确认。随后一次限定 GET 成功从对应蓝牙端口的现有设备记录读到测试名，补齐驱动层运行时解析名称的观测；未直接转储原始 DIB 内存。

## 为什么前几轮结果看起来互相矛盾

| 操作与事件 | 磁盘别名 | 运行时名称的机制推断 | 实际结果 |
| --- | --- | --- | --- |
| 首次联合写入 | 测试名 | 仍是此前的原名/空别名状态 | PnP 可暂时显示测试名 |
| 随后直接重连 | 测试名 | 旧运行时状态 | PnP 与实际列表恢复原名 |
| 电脑重启后 | 测试名 | 已保存别名被加载 | 测试名重新出现，后续一次重连保留 |
| 工具恢复两处原值 | 原始单字节 00 | 仍保留已加载的测试别名 | PnP 与实际列表当场恢复原名 |
| 恢复后再次重连 | 原始单字节 00 | 测试别名仍被优先采用 | PnP 与实际列表再次出现测试名 |

这组行为的区别在于运行时状态是否得到更新。重启后的成功不能说明直接改注册表已具备即时同步能力。

## 已定位的代码链

```text
BTHPORT 持久 FriendlyName
  → HCI_AddStoredDeviceToList / HCI_DibGetStoredDevInfoEx
  → DEVICE_INFO_BLOCK 的运行时别名
  → HCI_DibNotifyDeviceEnumerator（非空别名优先）
  → PnP 设备变化通知
  → LEEnumerator::RootFdo::s_PnPNotificationCallback
  → RootFdo::DistributeDeviceChangeNotification
  → DevicePdo::s_OnDeviceChangeNotification
  → DevicePdo::UpdateProperties / s_GenerateFriendlyName
  → IoSetDevicePropertyData(DEVPKEY_Device_FriendlyName)
  → PnP 名称与本机蓝牙列表
```

BTHPORT 把非空运行时别名填入返回设备信息的 LE 名称字段，并设置 BDIF_LE_NAME；只有别名为空才采用原始 LEName。枚举驱动生成友好名时优先使用该 LE 名称字段。

磁盘加载器要求别名类型为 REG_BINARY、长度不超过 248 字节，并对内部缓冲区最后一字节补 NUL。这是字节限制；不能用“248 个字符”代替。

## 现场写入证据

第二轮跟踪在本地时间 16:58:43–16:59:06 进行，用户在跟踪就绪后重连键盘。捕获完成后会话正常停止，丢失事件和缓冲均为零。

同一根节点 FriendlyName 的两次成功写入严格区分：

| 相对跟踪开始 | 写入者 | 结果与调用栈 |
| --- | --- | --- |
| 0.331527 秒 | DeviceRadar.NativeProbe，PID 44436 | 受守卫恢复原名的正对照，RegSetValue 成功，58 帧栈 |
| 17.317174 秒 | System，PID 4，TID 27960 | 键盘重连后的回写，RegSetValue 成功，26 帧栈，包含微软 LEEnumerator |

后一次写入的驱动帧为 RVA 0x181BD、0x54D4、0x1159E、0x12A65，分别对应 UpdateProperties、s_OnDeviceChangeNotification、DistributeDeviceChangeNotification 和 s_PnPNotificationCallback。

该次跟踪中，精确 BTHPORT 缓存 FriendlyName 的读取、写入均为零；写后回读仍是原始单字节 00。PnP 与解析名已变为测试名。目标家族 1999 条操作均匹配到栈，KCB 路径回填冲突为零。原始路径和地址只留在本机跟踪中，摘录已脱敏。

UpdateProperties 的实际帧比磁盘静态调用返回点多 5 字节。PE 动态重定位记录表明该位置允许加载时 import/CFG 调用改写，符合该差异；本轮未读运行时代码字节，因此不将其写成静态偏移的精确命中。两个未改写的直接调用返回地址完全对应。

## 驱动层名称已直接查询

本地时间 17:27:57，经过静态入口分析和独立边界复核，执行一次固定的旧式 GET `0x411058`：LE-only flags=2、version=3、非零封存目标、in/out=0x358，无生成记录标志。脚本先验证机器、恢复点精确 SHA-256、目标、OS build 与两份驱动文件指纹；只允许唯一 radio，并严格校验返回大小、数量、协议与地址。

查询成功，返回 flags `0x01539027`，Name 和 LeName 均为 `Device Radar 测试键盘`。随后独立回读磁盘别名仍为原始 `00`、PnP 仍为测试名。此请求仅查询现有驱动设备记录，没有调用运行时更新器、磁盘保存器、GATT 或缓存刷新。这确认了驱动当前提供的解析名称与磁盘别名不同；返回值不单独暴露原始 alias 和原始 LEName。

结果保存在 `driver-cached-name-get-01.json`；读取器 `Read-BthPortCachedName.ps1` 已实际编译并完成这一次 GET。未尝试其他私有 GET 布局、枚举所有设备或失败后猜测重试。

## 不重启修复应处理的层级

一次性同步运行时别名、持久别名和 PnP 名称，才有机会满足纯便携要求。单独重新写 PnP 或磁盘字段，会重复本次状态分离。

本机公开 BluetoothUpdateDeviceRecord 经 bthserv 到达 BTHPORT 的缓存更新器，但调用方固定选择经典蓝牙 PHY；不能把它直接当作当前纯 BLE 键盘的已验证入口。内部 HCI_DibUpdate 存在 LE 选择和别名清空分支，是后续可逆实验的候选，尚未执行。

还需区分运行时更新与持久化：内部 alias-only 更新分支没有直接保存磁盘。BTHPORT 另有 DIB 保存及 FDO 销毁时 StoreAndPurge 路径，可能把运行时状态重新落盘；不能泛化为每次电脑重启必定执行，也不能承诺只恢复磁盘后重启必定修好。服务/适配器重启同样尚未验证。

## 证据与当前状态

- [完整重连/重启/恢复实验](BLE-JOINT-LAB.md)。
- 本机忽略目录 `artifacts/device-lab/ble-rootcause-20261027/` 中保存匹配符号、反汇编笔记、两轮 ETL、零丢失统计、实际执行过的解析器与脱敏摘录。
- `research-runtime-symbols.md` 记录磁盘 → DIB → 通知 → PnP 的精确二进制 SHA-256、PDB GUID/age 与函数位置；`research-runtime-refresh.md` 记录更新、落盘和输入边界。
- `research-tracing-capture02.md` 和 `capture-02-target-summary.json` 记录实际 System 写入及完整帧。
- 辅助探针在指定 AssociationEndpoint 对象类型后，成功独立读取到 BluetoothLEDevice.Name 和配对端点名均为测试名（`runtime-name-probe-20261027-r4.json`）。此前包装器失败/超时记录保留，不作为设备行为。外围设备 GAP 名称读取仍未成功，不能视为已取得设备端的实测名称。

当前缓存别名为原始 00，驱动层解析名称、WinRT 名称和 PnP 均为测试名。回写者与对应运行时名称机制已定位；原始 DIB 字段及此前重启时的加载时点未被直接转储或捕获。原名的运行时同步修复尚未执行。本次两条取证会话均已停止，没有安装常驻修复程序。
