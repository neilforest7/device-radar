# 当前鼠标的持久性实验

本页保留原 NativeProbe 的历史实验与重启证据。2026-10-28 的真实产品 TUI、同口拔插和历史恢复验收另见 [ACCEPTANCE](ACCEPTANCE.md)，未将本页历史结果当作本轮重跑。

状态：已通过原生改名、写入进程退出后的独立回读、用户确认的设备管理器显示、同口拔插、电脑重启保留及原始属性恢复。

## 对象与范围

- 环境：当前电脑，Windows build 22631，x64。Windows 10 22H2 尚无测试环境，保留验收要求。
- 实物：用户确认是罗技 LIGHTSPEED USB 接收器连接的鼠标；公开硬件 ID 为 VID 046D / PID C547。
- 写入目标：该连接下唯一的 HID 鼠标节点，设备类为 Mouse。鼠标对应的零售型号尚未确认。
- 名称位置：设备管理器的鼠标节点。此实验不代表蓝牙设备本体或声音端点路径已验证。
- 新名：`Device Radar 测试鼠标`。

只读枚举发现 6 个 Mouse 类条目，包含键盘与数位板附带的鼠标接口，不能按设备类直接认定实物。已沿父子关系区分，并由用户确认当前鼠标。

## 写入前状态

- Windows 当前显示名称：`HID-compliant mouse`。
- PowerShell 与原生探针均确认独立 FriendlyName 属性不存在，类型为 EMPTY。
- 恢复点须保存属性的存在状态、类型与原始字节。原本不存在时，恢复应删除新增昵称，不能写入通用描述冒充原值。
- 当前进程非管理员；准备和检查可只读进行，系统写入须通过 Windows 管理员授权。

## 执行步骤与记录

| 步骤 | 当前状态 |
| --- | --- |
| 确认实物、连接与测试名称 | 已完成 |
| 只读检查设备条目与命名属性 | 已完成，原生 API 已复核 |
| 原生探针构建、纯内存检查 | Release 构建通过；22 项内存检查通过 |
| 保存原值并核对写入计划 | 已完成；原值缺失状态保留在不可变恢复点 |
| 单节点改名，结束写入进程 | 已完成；管理员写入进程退出码 0 |
| 独立进程重新读取 | 已通过；原生 API 与 Get-PnpDevice 均返回测试名，设备状态 OK |
| 设备管理器界面核对 | 用户确认已看到测试名 |
| 用户单独确认后拔插接收器，再读取 | 已通过；观察到 offline → unique，同一身份重新出现，原生只读回读仍为测试名 |
| 用户另行安排重启后读取 | 已通过；启动时间已变化，同一目标只读回读仍为测试名，Get-PnpDevice 状态 OK |
| 从原始恢复点还原并核对 | 已通过；管理员恢复进程退出码 0，独立原生回读确认 FriendlyName 已不存在，Get-PnpDevice 返回原名且状态 OK |

拔插接收器会暂时中断该鼠标操作，执行前单独说明并确认。设备重新出现时先核对身份，不自动重套测试名称。

本次只读观察在 2026-10-26 04:38:56 UTC 捕获目标离线，04:38:58 UTC 捕获重新出现；随后独立 verify 的 `observedState` 为 `proposed`，`writeAttempted` 为 `false`。写入进程已退出，观察程序不执行重命名。

完整标识和原始恢复数据仅保留在程序旁的本机实验数据目录；本页只记录公开标识和实验结论。

## 程序与保护检查

源代码及使用说明见 [NativeProbe](../prototypes/DeviceRadar.NativeProbe/README.md)。本机自包含程序位于 `artifacts/device-lab/DeviceRadar.NativeProbe.exe`。

- 非管理员真实调用 apply：按预期返回 `administrator-required`，写入次数为零；随后独立读取仍为原始状态。
- 恢复点和写入 intent 均采用 JSON 加 SHA-256 seal 提交；完整持久化、回读一致后才调用系统写接口。
- 已修正写入后进程中断的恢复缺口，以及路径检查与实际文件操作间的目录替换竞态。最终代码定点复核通过。
- 15 项纯本地文件检查通过，包括目录锁、链接拒绝、文件与 seal 句柄保护、路径逃逸拒绝及未完成记录处理；这些检查没有写设备属性。

尚未执行的步骤保持待验收，不以接口或回读成功替代持久性结果。

## 重启验证与恢复点

重启前读取的系统启动时间为 `2026-10-13T14:14:06.5000000Z`（UTC）；重启后为 `2026-10-26T04:53:08.5000000Z`，已确认本次电脑重启发生。

2026-10-26 04:57:40 UTC，独立原生 `verify` 返回 `observedState=proposed`、`writeAttempted=false`；目标身份校验通过，系统名称为 `Device Radar 测试鼠标`，FriendlyName 存在、类型为 18。独立 Get-PnpDevice 返回相同名称，设备状态 OK。此次重启后未重新应用昵称。

当前恢复点 token：`9bac2d9d8d954bb5a920b25bbf2c905d.json`。完整身份及原值留在 EXE 旁的本机 data 目录，JSON 与 `.sha256` 均由探针读取并校验。

本次重启后执行的独立只读核验：

```powershell
.\artifacts\device-lab\DeviceRadar.NativeProbe.exe verify --record '9bac2d9d8d954bb5a920b25bbf2c905d.json'
```

本次结果证明该电脑上这一 PnP 鼠标节点的昵称经过退出、同口拔插及重启仍保留。换 USB 端口、驱动和系统更新尚未验收；不扩大为所有设备或 Windows 版本的持久性结论。

## 恢复验收

用户选择恢复原名后，通过 Windows 管理员授权执行 `restore --record '9bac2d9d8d954bb5a920b25bbf2c905d.json'`，恢复进程退出码为 0。

2026-10-26 05:27:34 UTC，新的独立 `verify` 返回 `observedState=original`、`writeAttempted=false`，目标身份仍匹配恢复点。FriendlyName 为 `exists=false`、`type=0`，确认已删除本轮新增属性，恢复了原本缺失的状态。系统解析名称为 `HID-compliant mouse`；独立 Get-PnpDevice 也返回该名称和 OK 状态。

本轮改名、保留和恢复实验完成，鼠标已回到原始命名状态。本次恢复后的设备管理器界面未另行人工核对。
