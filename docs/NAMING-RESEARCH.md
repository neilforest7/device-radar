# Classic 蓝牙与播放端点命名研究

本页保留当时的研究和开放条件；现行后端范围与验收状态见 [SUPPORT](SUPPORT.md)。

2026-10-28，只读资料与本机清单核对。下述路径均未因本次研究执行系统写入。

## Classic 蓝牙

[BluetoothUpdateDeviceRecord](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothupdatedevicerecord) 更新本机蓝牙设备缓存，返回 `ERROR_SUCCESS` 只表示调用成功；文档未承诺 Windows 设置页面、重连或重配后名称。`BLUETOOTH_DEVICE_INFO` 要求 `dwSize` 为结构字节数、`Address` 为之前发现的设备地址、`szName` 为新名称。[结构](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/ns-bluetoothapis-bluetooth_device_info_struct)另有设备类别、连接/记忆/认证状态和时间；`szName` 为 248 个 WCHAR 的固定数组，需为结尾 NUL 留位置并按 UTF-16 单元校验。三个 `f...` 是状态字段，公开结构和更新 API 没有可由调用方传入的 rename flags，不要把内部驱动标志当作此 API 参数。

可实现的受限路径：通过 [BluetoothFindFirstDevice](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothfindfirstdevice) 等取得已知设备，通过 [BluetoothGetDeviceInfo](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothgetdeviceinfo) 读取本地记录，并以本机精确地址及目标实例关联；在预览、保存可恢复的原值、再次核对身份和现值后调用公开更新接口。返回后分开核验本地记录、PnP/WinRT 名称和实际设置页；再验工具退出、同配对重连及原值恢复。不能仅靠设备名称或相同型号定位，地址和实例只留本地。还需验证原值不存在或空别名时，公开 API 如何恢复原始状态；读取到的解析名称不能替代原始别名备份。

微软将这组 [Win32 蓝牙接口](https://learn.microsoft.com/en-us/windows/win32/bluetooth/about-bluetooth)用于 Classic BR/EDR；本项目 [BLE 根因调查](BLE-ROOT-CAUSE.md)亦发现本机该调用的后端固定选择 Classic PHY。不能将 Classic 结果扩展到纯 BLE 或双模设备的另一条连接。

本次只读清单发现 **1 个 present Classic PnP 功能与本机缓存可按地址关联**。尚未确认实际设备类别、用户是否能操作该实物、公开 API 是否能枚举此记录及跨重连身份；因此它只是候选，不是已授权或已验收的测试设备。下一步先做不包含私有标识的本机只读枚举与身份核对，用户确认实物后再安排单设备可逆实验。此路径在完成实验前保持不可写。

## 播放端点

微软 [Core Audio Device Properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties) 要求客户端读取而不设置基础属性；`DeviceDesc`、完整 `FriendlyName` 和适配器名称分别承担不同作用。Windows 原生界面可作为人工操作入口，但不能把它描述为公开的通用属性写入 API。

项目现有 [AudioPolicy.cs](../prototypes/DeviceRadar.NativeProbe/AudioPolicy.cs) 是受限、未公开 `IPolicyConfig` ABI：`f8679f50-850a-41cf-9c72-430f290290c8`，保持前序 vtable 槽位，`GetPropertyValue` / `SetPropertyValue` 的 `bFxStore=false` 在 key 前；使用完整 x64 `PROPVARIANT` 与主存储 `PKEY_Device_DeviceDesc`。本项目在 Windows 11 build 22631 的指定 USB **录音**端点验证写入、多个名称层和通知、界面刷新及恢复；本轮产品已复验同口拔插、历史恢复及恢复后拔插；电脑重启仍待验，详见 [实测记录](BLUETOOTH-AUDIO-LAB.md)。此证据不等于播放端点或其他 Windows 版本通过。

播放的具体下一步：只读选择一个明确的 render endpoint，记录端点 ID 与 PnP 对应关系、主存储原始值和各名称层；验证该 build 的 ABI 只读调用，再对用户确认的单端点执行备份、写入、独立回读、指定 Windows 播放页面观察、退出、重连及恢复。仍需检查活动播放、默认设备角色和通知是否受影响，确保恢复完整。完成前播放端点保持不可写。

2026-10-28 本机只读核对：已授权的 AB13X 设备组包含一个播放端点；`IMMEndpoint.GetDataFlow` 返回 render，`IMMDevice.GetState` 返回 `2`（`DEVICE_STATE_DISABLED`）。该端点仍可枚举，容器与 AB13X 设备组一致，PnP 根节点在线；此关联不是仅按名称推断。端点禁用期间尚未验证活动播放、实际输出及可逆改名；是否需要额外输出设备尚未确认。启用后需重新枚举并核对身份，再评估后续验收。本次没有启用设备或修改系统。注册表中的 `DeviceState` 与 Core Audio 返回值分别记录，不能直接按后者的枚举解码前者。

## 参考实现和许可证

| 上游 | 本次核查与使用边界 |
| --- | --- |
| [SoundSwitch](https://github.com/Belphemur/SoundSwitch/blob/ee7def135eb96c7ac951e2d1535c7a661dfefbef/SoundSwitch.Audio.Manager/Interop/Interface/Policy/IPolicyConfig.cs) | 接口声明可对照 vtable 和参数，项目许可为 GPL-3.0-or-later；不能直接将其实现按本项目 MIT 许可合并分发。其切换默认播放/录音设备的功能也不是端点改名验收。 |
| [AudioDeviceCmdlets](https://github.com/frgnca/AudioDeviceCmdlets/blob/26d08d30e9d804c016c994117f9b038b7d50da95/SOURCE/IPolicyConfig.cs) | 此提交的 [LICENSE](https://github.com/frgnca/AudioDeviceCmdlets/blob/26d08d30e9d804c016c994117f9b038b7d50da95/LICENSE) 为 MIT，Copyright 2016–2022 Francois Gendron；复用受版权保护代码时需保留原版权及许可。其接口定义证明一种 ABI 声明，不证明跨版本或播放改名有效。 |
| [Nodus](https://github.com/voidless-labs/Nodus/blob/main/src-tauri/src/virtual_audio/endpoint_name.rs) | 实现自述对 render/capture 调用此未公开接口；它的虚拟设备及自身实验不能代替本机硬件实测。2026-10-28 核查仓库元数据无 license，根目录无 `LICENSE`，[Cargo.toml](https://github.com/voidless-labs/Nodus/blob/main/src-tauri/Cargo.toml) 的 license 为空；未找到授权前不复用代码。 |

产品沿用本仓库原型的实现，接口 ABI 曾对照上述资料；未复制 SoundSwitch 或 Nodus 实现。[第三方声明](../THIRD-PARTY-NOTICES.md)附带 AudioDeviceCmdlets 的 MIT 版权与许可证。
