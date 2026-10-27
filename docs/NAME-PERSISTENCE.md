# 名称持久性：核心技术挑战

2026-10-28 更新：VGN 的原名运行时同步恢复已通过本机限定实验及一次正常重连，详见 [BLE-CURRENT-RECOVERY](BLE-CURRENT-RECOVERY.md)。下述未修复状态属于原历史；完整 BLE 改名往返与新路径重启仍未验证。首版真实产品的能力和检查见 [SUPPORT](SUPPORT.md)、[ACCEPTANCE](ACCEPTANCE.md)。

状态：鼠标已完成原生改名、退出、同口拔插、电脑重启及恢复实验，见 [实测记录](MOUSE-PERSISTENCE-LAB.md)。AB13X 麦克风已复现工具层的原生名称更新、系统通知、设置刷新及恢复；其存储生命周期与新版后端验收分别记录。VGN BLE 已定位到 BTHPORT 运行时别名与磁盘值不同步，重连由微软 BLE 枚举驱动回写 PnP；运行时同步修复仍待验证。产品目标仍为 Windows 10 22H2 起、Windows 11、x64，单次修改后退出，名称在目标 Windows 界面持续生效。

## 产品要求与判定

“系统后来恢复旧名”必须作为需要查明和解决的缺陷。回读成功、再次运行工具修复、仅在 TUI 显示私有昵称，都不能代替解决 Windows 界面的持久命名。

用户默认在回读后看到结果，界面核对与重连验证可选；开发阶段必须验证工具退出后的效果。普通退出、重启、同一配对重连、USB 拔插是每条宣称持久路径的基本开放门槛。换 USB 端口、驱动更新、系统更新、移除重配另行验证并准确记录边界。

若基本门槛失败，该路径保持实验状态，继续定位覆盖来源；不能仅加一条“可能恢复旧名”的说明后宣布支持。不同系统版本、连接类型与命名位置独立判定。任何后续事件仍无法保持的情况均登记为未解决项，不被当前基本门槛掩盖。

## 先区分名称为何失效

| 现象 | 需要取得的证据 | 修复方向 |
| --- | --- | --- |
| 同一实例的名称值变回旧名 | 身份未变，目标字段被再次写入 | 查明写入方和触发事件，寻找系统认可的用户覆盖层 |
| 系统创建了新实例或新音频端点 | 旧项仍有昵称，新项 ID 不同 | 验证系统能否迁移用户设置；研究稳定身份与重新关联 |
| 属性回读是新名，Windows 仍显示旧名 | 不同 API、目标界面读出的名称不一致 | 找到该界面实际采用的名称层，修正写入对象 |
| 属性与另一系统界面都已更新，仅一处迟迟不变 | 同一身份，刷新或重开界面后变化 | 验证刷新机制，不把缓存误判成持久存储失败 |

表中是诊断分支，不能仅凭名称不一致断定驱动覆盖。蓝牙本体、PnP 节点、播放和录音端点分别采样。

## 优先验证的持久命名路径

### 蓝牙本机显示名

微软区分设备报告名和用户本地提供的显示名；后者可通过 `BluetoothGetDeviceInfo` 读取。`BluetoothUpdateDeviceRecord` 接收已发现设备地址和新名称，更新本机记录。这给出了值得验证的本机命名层，不能将远端上报名和本地昵称混为一个字段。[BTH_DEVICE_INFO](https://learn.microsoft.com/en-us/windows/win32/api/bthdef/ns-bthdef-bth_device_info)、[BluetoothUpdateDeviceRecord](https://learn.microsoft.com/en-us/windows/win32/api/bluetoothapis/nf-bluetoothapis-bluetoothupdatedevicerecord)

验证顺序：Windows 自带属性页改名作为对照 → 公开 API 改名 → 定点用户别名存储候选。逐步核对本机记录、现代 Bluetooth 设置、设备容器和音频端点，定位谁采用哪份名称。公开 API 的存在尚不能证明现代设置页会采用它，也不能证明重连保留。

2026-10-27 重新核对现有项目源码：

| 项目 | 实际实现 | 对当前目标的帮助 |
| --- | --- | --- |
| [Dylan103 Bluetooth Renamer](https://github.com/Dylan103/bluetooth-renamer/blob/86787634a4f35ef8fe18fb8e657ae716d6536b73/README.md) | BTHPORT `FriendlyName` UTF-8+NUL 别名加 Classic `BluetoothUpdateDeviceRecord`；README 明确 BLE-only 不列出 | 可研究 Classic 路径；作者只记录 Win11/Galaxy Buds FE 重启保留，没有覆盖当前 BLE 键盘 |
| [BluFang](https://github.com/VAROIndustries/BluFang/blob/83ce7d0aab607fc0fad723a1ca518a95d755be6e/BluFang.ps1) | 联合写 BTHPORT `FriendlyName` 与 BTHENUM/BTHLE 的 PnP `FriendlyName`；没有另一套持久昵称存储或重连维持逻辑 | 仓库未提供 BLE 生命周期实测；本项目复现该组合加一次电脑重启后，VGN 的一次同配对重连保留已通过，恢复仍待验 |
| [32feet Windows 后端](https://github.com/inthehand/32feet/blob/3b589aff87b331c2326624acf929d16ac200736c/InTheHand.Net.Bluetooth/Platforms/Windows/BluetoothDeviceInfo.Windows.cs) | Windows `DeviceName` 包装原生只读 Name，当前无 setter | 可帮助蓝牙访问，未提供目标改名能力 |
| [bluetuith 的 Windows 库](https://github.com/bluetuith-org/libhbluetooth/blob/9c6b8547f91a9ac03ffcb2db63bbef54870eccfe/src/HBluetoothLib.Platform.Windows/Devices/DeviceMethods.cs) | Windows Classic 查询、连接与配对等；无 Rename/SetAlias | 跨平台管理能力不能当作 Windows BLE 持久改名实现 |

Dylan103 的[持久存储实现](https://github.com/Dylan103/bluetooth-renamer/blob/86787634a4f35ef8fe18fb8e657ae716d6536b73/Source/src/PersistentNameStore.cs)与[验证记录](https://github.com/Dylan103/bluetooth-renamer/blob/86787634a4f35ef8fe18fb8e657ae716d6536b73/Verification.md)可供核对字段和恢复方式；作者的单设备重启声明不扩展为 BLE 或重连保证。当前 Dylan103 和 BluFang 仓库都没有提供 LICENSE，复用源码前需明确许可。BluFang 的权限失败分支会接管注册表所有权及修改 ACL，这部分不作为本项目实现依据。

2026-10-27 使用本项目探针执行 [BluFang 联合方案受控实验](BLE-JOINT-LAB.md)。首次写入后直接重连会使 PnP 与实际蓝牙列表恢复原名；电脑重启后，在未重新 apply 的情况下测试名重新出现，并通过随后一次同配对重连的程序及实际列表验收。当前 VGN、Windows build 22631 的已验证流程包含一次电脑重启，无常驻维持程序。尚未证明两处写入均必要，或可用蓝牙栈刷新代替电脑重启。

原名恢复也呈现运行时缓存差异：两处原值恢复、独立回读与实际列表均通过，但再次重连使 PnP/实际列表回到测试名，磁盘缓存仍为原始 `00`。用户选择稍后重启，当前保留此状态；恢复未完整通过，等待重启后的独立核验。产品须区分“已写入，待重启电脑验收”和已完成状态。未运行第三方项目脚本，未将该限定组合扩展为通用 BLE 自动回退。

随后在不重启电脑的[根因调查](BLE-ROOT-CAUSE.md)中，本机二进制/公开符号确认磁盘别名加载到 BTHPORT DIB，通知生成优先取非空运行时别名；真实重连 ETW 捕获 System/LEEnumerator 成功回写同一 PnP FriendlyName，该次磁盘别名无读写且零丢失事件。直接写磁盘和 PnP 没有同步 DIB，是此次覆盖的原因。另有 FDO 销毁时的运行时状态落盘路径，不能承诺磁盘恢复后重启必定解决；需验证运行时别名、持久值、重连后 PnP 三者同步的可逆方法。内部 LE 更新分支尚未调用，公开 Classic API 在本机固定选择 BR，不能直接作为该 BLE 后端。

此后一次严格限定的驱动 GET 已实际成功：在磁盘别名为 00 时，BTHPORT 现有设备记录的 Name/LeName 均返回测试名，与 WinRT 和重连后的 PnP 对应。GET 不刷新或修改记录，原始 DIB 内存未被转储，运行时同步修复仍未执行。

Classic、BLE、双模设备及 LE Audio 分开验证；不能把同配对重连的结果扩大为移除重配或 BLE 地址变化后的保证。

此前 VGN N75 PRO-1 的单字段、不重启电脑实验发现：BTHPORT `FriendlyName` 别名跨键盘重连保留，但 PnP 和 WinRT 已配对列表仍显示旧名；该轮已恢复原始字节。此结果不能代替后续联合写入加电脑重启的验收；完整历史见[蓝牙与声音实测](BLUETOOTH-AUDIO-LAB.md)。

此前 PnP 单字段路径经用户确认在 Windows 蓝牙设置中生效，但正常重连把同一实例的 FriendlyName 重写回原名，当时尚未确定写入者，随后进行 LEName 来源实验。最新取证已定位 LEEnumerator 回写和 BTHPORT DIB 来源，见上述根因记录。实读 WinRT 对象 Kind 为 AssociationEndpoint，AEP 名称可与 Windows 设置页暂时不同，必须区分观测来源。

LEName 单字段实验已执行：测试值跨正常关闭再开启键盘后仍保留，Name、FriendlyName 控制字段未变，PnP 和 AEP 仍为旧名。随后电脑重启将同一设备的 LEName 刷新回原名，三个缓存字段及 PnP/AEP 均为原始状态；未重新应用昵称或执行工具恢复。此路径未通过重启持久性验收，继续查明刷新写入方和更上游的名称来源。

### 声音端点用户名称

微软说明，Windows 音频控制面板改名修改的是该音频端点的持久属性存储。优先复现 Windows 自身用户改名的效果，并比较同一端点的属性变化。[音频端点友好名](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/friendly-names-for-audio-endpoint-devices)

`OpenPropertyStore` 的读写能力不等于改名具有正式支持保证；Core Audio 文档要求客户端读取而不设置这些基础属性。因此具体自动写入方式需要实验验证，不能把通用 `SetValue` 当作已解决方案。[Device Properties](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)

本机 AB13X USB 麦克风已取得 Windows 原生改名对照：62 项端点 Properties 中只修改 DeviceDesc `,2`（REG_SZ），FriendlyName `,14` 仍缺失，Core Audio 解析出“用户名称 (AB13X USB Audio)”。该证据定位了本机实际字段；工具写入与持久性独立验收，见[实测记录](BLUETOOTH-AUDIO-LAB.md)。

v3 后端通过未公开的 `IPolicyConfig.SetPropertyValue` 在固定端点的主属性库写 DeviceDesc，已在 build 22631 普通权限下实测。它同步原始存储、Core Audio、PnP 和新鲜 WinRT 名称，并产生实际 Core Audio/WinRT 名称更新通知；各处不一致时拒绝计作同步成功。已打开的 Settings 进程仍可能保留旧值，本机已通过关闭进程再打开的对照确认。因此工具提供单独的“刷新声音设置”操作，确认后正常关闭已核验的 Settings 并重新打开声音页；写入和 UI 刷新分别记录，不能用回调数代替实际显示验收。此接口没有跨版本公开保证。

端点重建与原值被覆盖须分开。微软说明，普通端点 ID 可在驱动更新或重装时变化；音频迁移会尝试复制系统认识的用户属性，但 reference string 或 connector pin ID 变化时新端点不继承原设置。文档未单独保证友好名必然参与每次迁移。[端点 ID](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings)、[音频设置迁移](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/operating-system-upgrades)

Windows 11 24H2（build 26100）起可尝试读取 `PKEY_AudioEndpoint_StableId`，用于更新后重新找到端点。该属性可能缺失或变化；它不迁移或固定昵称，Windows 10 路径也不能依赖它。[StableId](https://learn.microsoft.com/en-us/windows/win32/coreaudio/pkey-audioendpoint-stableid)

### PnP 实例友好名

`SetupDiSetDeviceProperty` 可设置当前实例的 FriendlyName。设备安装 INF 也可写入这一名称，因此需要检查安装或更新是否重写，以及是否产生新实例。INF 的 `NOCLOBBER` 是驱动作者控制该安装项是否覆盖的标志，不能作为工具能给任意现有驱动开启的全局开关。[FriendlyName](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-friendlyname)、[INF AddReg](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/inf-addreg-directive)

## 针对性实验

以下为实验方法，完成状态和实际结果记录在各设备的实验文档中。

1. 选定每类测试设备和明确的目标系统界面。记录系统 build、驱动版本、连接类型、实例/端点/容器标识、各名称现值及允许的恢复信息。
2. 先走 Windows 自带改名入口，保存前后差异。该结果是原生用户命名行为的对照，不将手工操作替代自动化实现。
3. 恢复后，一次只验证一条工具写入路径，使用可识别的测试名称；记录所有涉及字段的原值、类型、接口结果与回读。
4. 退出写入程序，由独立的只读探针重新枚举，并核对目标 Windows 界面。探针不得自动重套历史昵称，防止掩盖实际重置。
5. 对同一恢复点依次检查重连与重启。只有发现不保留或相关生命周期疑点时，增加对应的名称刷新、换口、驱动更新或移除重配实验。破坏配对或驱动状态的实验使用专用测试设备与环境。
6. 发生失效时比较事件前后的身份和名称；必要时用 Process Monitor 针对已定位的字段跟踪写入方，结合设备安装日志确定是否重装。没有证据时不把值变化归因于 Windows 或驱动。
7. 找到修正路径后重跑原失败事件，并验证恢复原名。记录完整结论：界面、原生路径、触发事件、是否需工具运行、保留结果、恢复结果。

每类设备从一个最小复现实验开始；出现不同覆盖机制再增加样例。蓝牙耳机需分别观察本体、播放与录音名称，不能相互替代。

## 无驻留方案的边界

优先采用 Windows 自身保留、并能在相关生命周期中迁移的用户命名层。

用户已选择当前坚持纯便携。原生持久方案的验证先完成，再依据具体失败机制讨论后备方案；当前验证不依赖计划任务或后台服务维持名称。

如果实验证实某位置只有会被重建的缓存，且 Windows 不保留用户覆盖或迁移记录，已经退出的工具没有执行机会为新位置重写昵称。稳定身份能帮助重新找到目标，不能让未运行的程序自动执行迁移。这是执行机制的限制，仍需具体到命名位置证明，不能据此提前否定全部设备。

后续如需评估事件触发修复任务或后台服务，须先给出具体位置的原生路径试验和失败证据。届时需评估持久程序路径、权限、修复延迟、主动改名冲突与卸载机制，再由用户选择。它们提供的是重新应用能力，不能计入纯便携路径的持久性验收。

通过修改注册表权限阻止系统写入，不能解决新实例生成和多名称来源问题，也可能阻碍驱动维护，不作为通用持久化路径。

## 当前进度与下一步

已完成：名称层与生命周期文档核查；确认声音用户名称存储、Classic 蓝牙本机显示名及定点别名字段。SoundSwitch 和 AudioDeviceCmdlets 提供了本次音频策略 ABI 的源码对照，Nodus 提供实际 DeviceDesc 改名调用；本项目已自行完成限定设备的工具与界面实测。这些结果仍不构成通用抗覆盖方案。

当前鼠标已通过单个 PnP 实例的写入、写入进程退出后的独立回读、用户确认的设备管理器显示、同口拔插及电脑重启保留。重启后先确认系统启动时间变化，再由原生探针和 Get-PnpDevice 独立只读核验，未重新应用昵称。恢复也已通过：删除新增 FriendlyName，独立核验确认原属性缺失、系统解析为原名且设备状态正常。换 USB 端口、蓝牙和声音路径、Windows 10 环境及更新后的迁移仍待验证。

AB13X USB 麦克风的 v3 工具路径已通过：普通权限原生改名、原始存储/Core Audio/PnP/WinRT 名称一致、实际系统更新通知、工具刷新后的 Windows 声音设置显示、原生恢复、恢复后的独立读回与再次界面显示。单文件程序也已独立跑通，最终为原名，62 项端点属性相对本轮基线差异为零，进程已退出。早先直接写同一 DeviceDesc 的同口 USB 拔插和电脑重启保留记录继续保留；本次新后端未再次物理拔插或重启电脑，不混记。VGN BLE 键盘的 PnP 显示改名会被正常重连覆盖，LEName 缓存也在电脑重启后刷新回原名，仍为未解决项。[蓝牙与声音实测](BLUETOOTH-AUDIO-LAB.md)

当前 5 个实验入口、3 类写入接口及推进安排统一维护在 [设计计划](../DESIGN.md#按命名位置选择路径)。优先完成新音频后端的生命周期复验，并将已验证的鼠标、录音端点接入真实 TUI；BLE 来源诊断独立推进。随后补 Windows 10、播放端点、Classic 蓝牙及更多硬件验收。每次保持“只读采样 → 单位置可恢复写入 → 退出 → 触发事件 → 独立核验”的顺序，不将当前限定设备的结果扩大为所有设备与系统版本均通过。
