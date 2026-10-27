# 真实设备识别与改名流程

状态（2026-10-28）：本页流程已接入真实产品；设备接口通知触发重枚举，并定期对账。命名按本机验证配置开放，弱身份恢复前由用户确认实物与所选历史。实际能力与验收见 [SUPPORT](SUPPORT.md)、[ACCEPTANCE](ACCEPTANCE.md)。Windows 10 22H2 为兼容目标，尚未实测。

## 从设备列表到确认实物

1. 启动检测系统、权限和记录目录。先用本机信息显示列表；公开资料补全在后台进行，不阻塞浏览。
2. 订阅设备变化并取得初始清单。事件触发后重新枚举、对账与去重；一件设备的多个功能节点不能被误报成多件新设备。
3. 对照容器、父子关系、连接和可读标识，聚合有可靠关联的功能。型号识别、实物身份、可改名位置分别判断。
4. 详情列出每个位置的系统现名和可用操作。型号不唯一时，提供有依据的几个最可能完整型号供选择，也可手工命名；候选资料不足时不补造。资料后来补全时不覆盖用户已输入的名称。
5. 身份不确定时进入“查找实物”：记录当前清单 → 引导一次只断开一件 → 显示变化条目 → 引导接回 → 确认对应条目。没有看到明确变化则继续等待或重新尝试，不推断成功。
6. 拔插确认用于确定实物对应关系，不会补造型号或永久身份。缺少稳定身份时保留当前连接范围；恢复历史名称前重新确认，不能凭同型号或同端口自动套用旧记录。

离线、内置与虚拟设备继续通过筛选访问。离线条目是否可写由具体位置的能力决定，需要在线的路径先提示连接。

微软将 Container ID 定义为物理设备实例相关 devnode 的聚合标识；Hardware ID 服务于驱动匹配。实例 ID 的具体内容可能含序列号，也可能依赖位置，不能把这些标识混用。[Container IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/container-ids)、[Hardware IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/hardware-ids)、[Instance IDs](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/instance-ids)

通知与初始枚举之间可能重复报告设备到达，因此采用重枚举与去重。这里是实现方案，依据微软的通知时序说明。[到达与移除通知](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/registering-for-notification-of-device-interface-arrival-and-device-removal)

## 从改名预览到分项结果

1. 用户打开独立改名对话框。每个系统位置逐项显示：是否修改、当前名、新名称。可修改项默认选中；不可修改项显示原因和可用入口。
2. 预览固定本次目标、所选位置、旧名、新名和中断影响。后台设备变化保留输入；目标消失、身份或现名变化时暂停，重新确认后更新预览。
3. 确认后重新解析当前设备和目标字段，核对身份、权限、系统能力及现值。持久保存此次操作的恢复点和各次尝试前的快照，随后才写入。
4. 各位置按适用的公开接口、经实测且限定版本的内部接口、已验证的定点注册表方式选择路径。已知无效或会被覆盖的实验路径不自动尝试。需要断开或重启设备时另行说明、确认；取消该步骤则保留已完成项。
5. 每次尝试后重新读取。目标值一致则记录该层验证结果；明确没有改动时才继续下一条适用路径。值发生意外变化、无法读取或设备身份变化时暂停该位置，先查清状态，不连续盲试。
6. 分项展示结果。保留已成功项；失败或无法核验的位置提供重试、恢复或对应 Windows 入口。没有可改名的系统页面时准确说明，不能将打开设置当作已完成改名。

记录文件不提供任意注册表路径或命令；恢复和备用写入均通过当前设备重新解析允许的目标。

## 哪些结果能够说明生效

| 证据 | 能够确认的范围 |
| --- | --- |
| 接口返回成功 | 调用完成，仍须回读 |
| 系统值回读一致 | 目标值已保存；尚不能证明指定界面采用该值 |
| 指定 Windows 界面已确认 | 蓝牙、播放、录音或设备管理器中的目标位置已显示新名 |
| 工具退出后重新读取一致 | 无需工具驻留即可保持当前配置 |
| 设备断开重连后仍一致 | 此设备、连接、系统版本下的重连保留已验证 |

默认回读后显示结果，界面核对与重连验证可选。运行时只显示已取得的证据；未执行的检查标为未验证，不算失败。开发验收仍需验证持久性并记录系统版本、设备和路径，细则见 [名称持久性](NAME-PERSISTENCE.md)。

回读须标明具体名称层与 selector/object Kind。实测 BLE 的 WinRT AssociationEndpoint 名称可与 Windows 设置页显示不同；声音端点的 Core Audio、原始属性与 PnP 名称也可暂不同步。不能把任意一个同名 API 字段作为所有系统界面的验收依据。

声音回读包含 Core Audio Description/完整名称、PnP DeviceDesc/FriendlyName/DEVPKEY_NAME；开发验收另取新鲜 WinRT DeviceInterface 的 Name、ItemNameDisplay 和 Interface FriendlyName，并观察更新通知。即使这些来源已一致，已打开的 Settings 进程仍可能保留旧值。结果提供“刷新声音设置”，确认将关闭并重新打开后执行；不把单次回调或启动请求当作界面已刷新证据。

VGN/build 22631 的 BLE 联合流程包含一次电脑重启：写缓存别名与 PnP 名称后，初始结果显示“已写入，待重启电脑验收”，保存恢复点及待办后允许退出，由用户另行安排重启。重启后仅重新枚举和核验，不自动重写昵称。该组合已通过重启后的实际蓝牙列表和一次同配对重连；恢复原值后重连又出现测试名，恢复的重启验收仍待完成。不能将其扩展为所有 BLE 的已验证能力，或将未测试的蓝牙栈重启替代电脑重启。

后续[根因取证](BLE-ROOT-CAUSE.md)已定位 BTHPORT 的运行时别名与磁盘值未同步，以及 LEEnumerator 重连回写。上述结果只保留为实验历史，完整可逆路径仍待验收；在同步运行时别名的修复通过前，不向普通用户承诺“重启即可完成恢复”。

当前接口依据：

- PnP：`DEVPKEY_Device_FriendlyName` 是具体设备实例的属性，文档给出安装程序使用的读写接口；设置 API 要求管理员权限。设备管理器等界面的实际取名仍须核对。[FriendlyName](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-friendlyname)、[SetupDiSetDeviceProperty](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdisetdevicepropertyw)
- 蓝牙：`BluetoothUpdateDeviceRecord` 更新本机缓存记录；文档未承诺现代蓝牙设置页和重新配对后的显示。该调用只能作为候选路径，产品目标仍是指定 Windows 界面的名称生效。[BluetoothUpdateDeviceRecord](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothupdatedevicerecord)
- 声音：端点有独立属性存储，Core Audio 文档要求客户端读取而不设置基础属性。当前通过另行实测的内部音频策略接口修改 AB13X 录音端点，限定 build 22631；公开属性读取、内部写入和实际设置显示分别验收。[Device Properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)

系统保留的 `DeviceDesc` 不能作为通用改名字段直接覆盖。[设备属性修改规则](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/modifying-device-properties)

## 恢复与外部改动

- 每次修改保留独立恢复点。恢复时列出历史，由用户选择版本，预览该版本的各位置名称后执行。
- 写入前的现值若与预览不同，重新预览确认；恢复前的现值若与工具最后确认的值不同，提示可能存在后续改名，不自动覆盖。
- 弱身份匹配、不同电脑、目标位置变化不能直接恢复；先重新解析和确认。
- 恢复按位置执行并回读。成功项保留，失败项明确报告；原值不存在时恢复“不存在”，不写成空字符串。

## 名称被系统覆盖

持久性按核心技术挑战处理。先区分原位置被覆盖、新实例没有继承昵称、界面采用另一名称源及界面缓存，再验证对应的持久用户命名路径。再次打开工具恢复旧设置只是补救；完整研究与验证门槛见 [名称持久性](NAME-PERSISTENCE.md)。

当前坚持纯便携，先完成原生持久方案验证，再讨论是否需要额外修复机制。

## 后续验证顺序

先完成名称持久性的针对性验证，优先蓝牙和声音；并行做真实只读设备清单，验证外设筛选、功能聚合与拔插对应。用 PnP 路径贯通备份、写入、回读与恢复。确认“改完退出”的实际效果后再扩展其他类别。
