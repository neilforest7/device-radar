# 蓝牙键盘与 USB 麦克风命名实验

状态：AB13X 麦克风的 v3 原生音频策略写入、各名称来源同步、系统通知、声音设置刷新和原名恢复已通过工具级实测，当前保留原名。此前同一 DeviceDesc 存储的同口 USB 拔插和电脑重启保留已通过；新后端未再次单独复验，分别记录。VGN 的联合写入加电脑重启已通过一次后续重连，但恢复后重连又出现测试名。现已完成[根因定位](BLE-ROOT-CAUSE.md)：BTHPORT 运行时别名与磁盘值不同步，LEEnumerator 在重连时回写 PnP。运行时同步修复尚未执行。

## 当前对象

环境：当前电脑，Windows build 22631、x64。

| 实物 | Windows 当前名称 | 目标位置 | 已确认状态 | 测试名 |
| --- | --- | --- | --- | --- |
| VGN 蓝牙键盘 | Device Radar 测试键盘（恢复后重连的回读及实际列表） | Windows 蓝牙设备列表 | 缓存已恢复原值，运行时别名未同步；已定位回写，修复待验 | Device Radar 测试键盘 |
| AB13X USB 录音设备 | Microphone (AB13X USB Audio) | Windows 声音输入设备列表 | Core Audio ACTIVE；三个角色的默认录音端点；PnP 父链指向 USB Audio 和 USB Composite Device | Device Radar 测试麦克风 |

VGN 使用 BLE，不能用 Classic 蓝牙缓存 API 的结果代替其实际命名验证。键盘的 PnP 节点名称与 Windows 蓝牙列表显示名分别观测。

AB13X 的只读端点属性为：FriendlyName=`Microphone (AB13X USB Audio)`、DeviceDesc=`Microphone`、DeviceInterface_FriendlyName=`AB13X USB Audio`，均为 VT_LPWSTR。端点与其 PnP 节点的 ContainerId 相符。系统解析的属性值与持久存储中的覆盖值需要分别记录；生成的显示名不能直接当作原始覆盖值。

只读映射的 USB 标识为 VID 001F / PID 0B21。Capture 端点 Properties 共 62 项，`{a45c254e-df1c-4efd-8020-67d146a850e0},14` 原本不存在；`,2` 为 REG_SZ `Microphone`。最初 v2 原始快照 token 为 `f7d64e89e46541b78ba0a5609503f214.audio-snapshot.json`，保留全部原始属性、身份和 `.sha256`；v3 单文件程序的当前基线与记录见下文。

VGN 的原始 BTHLE 根节点 FriendlyName 为 REG_SZ `VGN N75 PRO-1`。BTHPORT 缓存的原始 Name 与 LEName 均为 UTF-8 加结尾 NUL 的 REG_BINARY，FriendlyName 为 REG_BINARY 单字节 `00`。单字段实验结束时均回到原始状态；2026-10-27 联合实验的当前状态见[单独记录](BLE-JOINT-LAB.md)。

完整实例、地址、端点和容器标识仅留本机恢复数据；文档不收录这些唯一标识。

## 实验顺序

1. 核对唯一目标、原始属性存在状态、类型和原始字节，以及 Windows 实际显示位置。
2. 保存不可变恢复点，重新读取并核对后，再执行限定字段的改名。
3. 退出写入程序，独立只读回读，再核对对应 Windows 界面。
4. 分别确认键盘断开重连、麦克风 USB 拔插的中断影响，再观察相同身份和名称是否保留。
5. 电脑重启由用户另行安排；回来后先核对启动时间并只读检查，避免重新套用昵称掩盖重置。
6. 用户选择恢复点后恢复并核验。原覆盖值不存在时恢复缺失状态。

每个命名位置独立记录结果。若原值被覆盖、系统创建新实例或界面采用其他名称，按[名称持久性](NAME-PERSISTENCE.md)的分支继续定位。

## 当前核验边界

Windows 设置窗口最小化，未取得有效目标画面；经典声音控制面板已通过可访问性文字和窗口截图确认 AB13X 是默认录音设备，名称行显示 Microphone、适配器行显示 AB13X USB Audio。后续自动交互未完成，原生改名对照由用户操作；程序回读不代替改名后的界面验收。

## 实测进度

### BLE 键盘

- 恢复点：`086bb848c7e84fa9ad138a40f691e290.json`；原始 FriendlyName 为 REG_BINARY 单字节 `00`。
- 定点代码复核通过；管理员写入返回 API 成功，结果为 `storage-confirmed-ui-unverified`，按设计退出码为 1，避免将原始值写入成功计为界面成功。
- 2026-10-26 07:05 UTC，新进程回读 `observedState=proposed`，但 PnP 名称及独立 WinRT 已配对列表名称仍为 `VGN N75 PRO-1`。配对对象与 PnP 身份均匹配，设备状态 OK。
- 用户确认正常关闭再开启键盘，保留原蓝牙通道和配对。2026-10-26 07:08:36 UTC，只读检查发现原始别名存储仍是测试值，PnP 根节点和独立 WinRT 配对列表均仍为旧名；此次事件没有观察到别名值被覆盖，当前字段也没有使目标列表采用昵称。
- 2026-10-26 07:11 UTC，已执行恢复并由新进程确认 `observedState=original`、`resolvedState=original`，原始 REG_BINARY 单字节 `00` 已恢复。下一步另存恢复点，验证 PnP FriendlyName，继续独立读取配对列表。

#### PnP 路径

- 新恢复点：`7a04a7439d0342858bb665712bf4d01a.json`；目标为同一 BTHLE 根节点的 `DEVPKEY_Device_FriendlyName`，原始 DEVPROP_TYPE_STRING 值为 `VGN N75 PRO-1`。
- 2026-10-26 07:28 UTC，工具写入后退出；独立回读确认原始属性和 PnP 名称为 `Device Radar 测试键盘`，用户确认 Windows 蓝牙设置页显示新名。未绑定恢复点的 inspect 也能继续唯一识别该键盘。
- 同时，WinRT `BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)` 返回的配对对象名称仍是 `VGN N75 PRO-1`。后续实读 `DeviceInformation.Kind` 确认为 AssociationEndpoint（AEP），AEP Name 与 ItemNameDisplay 均是原名；此处曾按选择器名称误记为 DeviceInterface，现以实际 Kind 修正。该接口的 Name 不是本次 Windows 设置页采用的同一名称层，不能用它单独推翻设置页已生效的人工证据。
- 用户再次确认通过原通道、原配对正常关闭再开启键盘。2026-10-26 07:30:46 UTC，新进程核验发现同一实例、硬件和容器下的原始 FriendlyName 已回到 `VGN N75 PRO-1`，`observedState=original`、`resolvedState=original`。工具没有重套昵称。
- 这是同一实例的命名属性被重写，不是生成新实例，也不是单个界面未刷新。当前没有证据确定实际写入进程或驱动；该路径不计作持久改名支持。键盘当前名称已是原名，来源是本次重连覆盖，不是工具执行恢复。

#### 名称来源调查

- 只读实测 AEP 的 IsPaired、IsConnected 均为 true。Aep.ContainerId 与 PnP ContainerId 转为 GUID 后严格相等；不同字符串格式、大小写生成的短 hash 不能直接拿来判断容器变化。
- 同容器 HID 提供公开型号标识 VID 320F / PID 5055 / REV 0104，可作为不依赖可改名称的正向型号约束。当前驱动服务为 BthLEEnum，但不能仅凭归属认定其为实际覆盖写入者。
- 本机 bthport.sys 出现 LEName、FriendlyName 字符串，说明实现识别这些字段；未找到微软对 LEName 优先级或持久性的公开契约。
- 下一步做 LEName 单字段因果实验：封存 Name、LEName、FriendlyName、PnP 名称与身份；仅改 LEName，保持 Name 工厂值和 FriendlyName 原始 `00`，再检查正常重连后字段是否被刷新、PnP 是否自动采用昵称。此实验不预先计作持久改名支持。
- WPR Registry profile 可用，Procmon 未在 PATH 和常见安装根发现；尚未启动系统跟踪。若单字段实验仍无法定位，再针对这些字段采集写事件及调用栈。现有日志没有提供实际名称写入者的证据。

#### LEName 单字段实验准备

- 程序、恢复守卫和 UTF-8/NUL 格式校验已完成并复审；最终 Release 构建无警告，39 项纯检查通过，未增加测试框架。
- 源状态完整快照：`e13a7d7f33a249aeb944ce30e050b04d.ble-snapshot.json`，包含三项原始缓存值、PnP 原始 FriendlyName 和设备身份，配套 `.sha256` 已保存。
- 单字段恢复点：`f0fbcb9eb388455e9f759e483c9af3af.json`，仅允许写现存 LEName。Name 必须保持原厂值，FriendlyName 必须保持原始 `00`，同容器 HID 型号及根身份须继续匹配；变化时拒绝继续写入。
- 首次 Windows 管理员授权取消，写入子进程没有启动。2026-10-26 07:58:30 UTC，新进程核验 `observedState=original`、`resolvedState=original`，LEName 与 PnP 名称仍为原值。
- 用户随后明确选择重新发起管理员授权继续该实验。2026-10-26 08:10:51 UTC，写入进程退出后，新进程核验 `observedState=proposed`、`resolvedState=original`，仅 LEName 已是测试值。独立来源采样确认：LEName 为 REG_BINARY、26 字节、UTF-8+NUL 的 `Device Radar 测试键盘`；Name 保持原始 14 字节 `VGN N75 PRO-1`，FriendlyName 保持单字节 `00`；PnP 与 AEP 名称仍为旧名。退出码 1 对应字段存储已确认、目标显示尚未确认，不能按此宣布界面改名成功。
- 用户已确认正常关闭再开启键盘。2026-10-26 08:12:40 UTC，只读回读仍为 `observedState=proposed`、`resolvedState=original`；独立来源采样确认 LEName 保留 26 字节测试值，Name、FriendlyName 未变，PnP 与 AEP 仍为 `VGN N75 PRO-1`。未重新套用昵称。
- 本次已证明 LEName 值可跨键盘正常重连保留，但尚未使目标显示名改变。下一步通过正常电脑重启，区分当前内存缓存未重新加载与该字段不控制目标显示；未执行重启前，不能把此结果计作持久显示改名成功。
- 用户完成 Windows 重启后，启动时间从 `2026-10-26T04:53:08.5000000Z` 变为 `2026-10-26T08:49:44.5000000Z`，系统 build 仍为 22631。2026-10-26 08:54:16 UTC，未重新应用昵称的新进程核验为 `observedState=original`、`resolvedState=original`；目标实例、硬件及容器身份匹配。
- 08:54:19 UTC 独立采样确认：LEName 已从 26 字节测试值恢复为 14 字节 UTF-8+NUL 的 `VGN N75 PRO-1`；Name 仍为原始 14 字节值，FriendlyName 仍为单字节 `00`；PnP 根名与 AEP 名称均为 `VGN N75 PRO-1`。本轮没有执行恢复或重套昵称。LEName 路径未通过电脑重启持久性验收，当前证据只能确定重启期间被刷新，尚未确定写入方及更上游的名称来源。

### USB 麦克风

- 用户已通过 Windows 声音属性页完成原生改名。
- 2026-10-26 07:03 UTC，原始快照与现值的 62 项比较只发现一项改变：`{a45c254e-df1c-4efd-8020-67d146a850e0},2`，REG_SZ，从 `Microphone` 变为用户填写的名称。`,14` 仍不存在。
- Core Audio 完整显示名随之成为“用户名称 (AB13X USB Audio)”。这证明本机原生用户改名修改 DeviceDesc，而不是插入 FriendlyName 覆盖。
- 工具将只复现此处已实测的定点字段；先回到原始 `Microphone` 状态，再执行工具改名、独立回读与持久性实验。原始完整快照继续保留。
- 复原基线的准备记录为 `11b560e9f40c4114909ae0781d0550eb.json`。首轮管理员调用未成功，新进程确认属性和解析名称均仍为调用前状态；完整 62 项差异也保持不变。事件已记录错误，尚未将此结果计作工具改名或恢复成功。
- 将写句柄改为原生 RegOpenKeyExW 的最小 QUERY_VALUE | SET_VALUE 权限后，管理员权限探针确认可打开写句柄，`writeAttempted=false`，没有调用 Set。随后该记录的基线复原执行成功；2026-10-26 07:23:30 UTC，完整 62 项与最初快照的差异为零，解析名称也回到 `Microphone (AB13X USB Audio)`。
- 真正的工具改名恢复点为 `f37c07f85fe049c082c47b8ff1c6677f.json`：原始 DeviceDesc=`Microphone`，测试名=`Device Radar 测试麦克风`。管理员写入退出码 0；2026-10-26 07:24 UTC，独立进程回读原始属性与 Core Audio 解析名一致。PnP 名称当时仍是手工对照时的名称，记录为跨来源同步差异。
- 用户随后确认 Windows 声音设置的输入设备显示 `Device Radar 测试麦克风`，并确认同口拔插 USB。
- 2026-10-26 07:27:02 UTC，工具只读核验 `observedState=proposed`、`resolvedState=proposed`，身份与两个 USB 祖先一致。独立 Get-PnpDevice 也返回 `Device Radar 测试麦克风 (AB13X USB Audio)`、状态 OK；此前的 PnP 旧名已同步。此次检查没有重新应用昵称。
- 2026-10-26 07:58:30 UTC，再次只读核验仍为测试名；本轮退出时暂保留测试名，电脑重启与最终恢复等待用户选择。
- 2026-10-26 08:13:43 UTC，再次独立只读核验仍为 `observedState=proposed`、`resolvedState=proposed`，身份一致；准备与键盘缓存来源实验一起安排电脑重启验收。
- 用户完成上述 Windows 重启后，2026-10-26 08:54:16 UTC，新进程核验仍为 `observedState=proposed`、`resolvedState=proposed`，原始 DeviceDesc 和 Core Audio 解析名均保留测试值，身份及两个 USB 祖先匹配。独立 PnP 枚举返回 `Device Radar 测试麦克风 (AB13X USB Audio)`、状态 OK。未重新应用昵称，已通过此次电脑重启的存储和系统接口验收；重启后声音设置界面核对及最终恢复等待用户选择。
- 用户随后确认重启后的 Windows 声音设置仍显示 `Device Radar 测试麦克风`，补齐本次重启后的界面验收，并明确选择恢复最初原名。
- 管理员恢复使用 `f37c07f85fe049c082c47b8ff1c6677f.json`，退出码 0。2026-10-26 09:18:38 UTC，写入程序退出后的独立核验为 `observedState=original`、`resolvedState=original`，Core Audio 名称恢复为 `Microphone (AB13X USB Audio)`，目标身份和 USB 祖先继续匹配。
- 原始完整快照的 62 项比较显示：名称字段均恢复基线，仅 `{194ef948-7cdb-403e-9f47-19418f7b24fd},1` 和 `,2` 两个 16 字节 REG_BINARY 值不同。尚未确定这两项差异的原因；不能将重启、设备使用等事件后的完整差异归因于本次恢复，也不回写未授权字段。
- 同时 PnP 枚举仍为测试名、状态 OK，记录为待同步的设备列表缓存；声音设置恢复后的实际显示，以及用户同口拔插后的保留与 PnP 同步，等待核验。
- 用户关闭再打开 Windows 声音设置后，确认仍显示 `Device Radar 测试麦克风`。2026-10-26 09:22:32 UTC 再次只读核验：原始字段与 Core Audio 仍为原名，PnP 仍为测试名。恢复的即时界面验收失败，不能将存储及 Core Audio 成功计作完整恢复成功；当前定点写入方式存在名称来源同步缺口，需调查 Windows 原生命名的更新/通知机制。用户同口拔插尚未确认，不能预先记为已刷新。
- 只读代码及资料复核：当前路径只调用 RegSetValueExW/RegFlushKey，没有经 Core Audio 的属性存储 setter。微软[记录了 SetValue 后的属性变化通知](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nf-mmdeviceapi-immnotificationclient-onpropertyvaluechanged)，但没有说明直接改注册表对现代设置页的同步行为，因此“缺少对应通知”只是待验证原因。[端点属性指南](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-properties)又要求客户端读取而不设置基础属性；不能据此将 SetValue 或未公开 IPolicyConfig 当作已确认的产品修复。尚未执行这些候选调用或任意广播，先等待用户同口拔插后的独立观测。
- 用户随后确认 AB13X 已拔下再插回同一个 USB 接口。2026-10-26 09:35:48 UTC，只读核验为 `observedState=original`、`resolvedState=original`，声音接口返回 `Microphone (AB13X USB Audio)`，身份及两层 USB 祖先匹配；独立 PnP 枚举也已恢复同一原名、状态 OK。没有再次写入或重新执行 restore。此次重新连接使 PnP 与已恢复的持久值同步，恢复后的原始字段、Core Audio、PnP 及同口重连保留验收通过；拔插后现代声音设置的最后界面核对待用户确认。即时同步缺口仍需解决，不能将依赖重新连接的结果记作即时恢复通过。
- 用户在此次同口拔插后确认：重新打开 Windows 声音设置，仍显示 `Device Radar 测试麦克风`。09:38:27 UTC 再次只读核验，原始字段、Core Audio 和 PnP 均为 `Microphone (AB13X USB Audio)`、状态 OK、身份匹配。故拔插只解决了 PnP 同步，未通过现代设置页恢复验收；不能把三项程序回读一致当作目标界面已恢复。
- 设置进程 PID 的只读观测显示，其启动时间为 08:54:02 UTC，早于本次 09:18 的恢复和 09:35 的拔插；用户重开窗口没有使其进程重新启动。进程内缓存为待验证原因，尚无证据排除其他名称来源。CUA 未取得有效设置窗口状态，截图为其他前台内容，不能作为设置页证据；已请用户将目标输入列表保留在前台，等待直接观察。
- 对精确同一音频端点继续只读检查，发现另一处残留：PnP `DEVPKEY_Device_DeviceDesc` 仍是测试名；09:48:29 UTC 精确回读确认为早先手工对照的 `Device Radiar 测试麦克风 (AB13X USB Audio)`（含原始拼写错误）；`DEVPKEY_Device_FriendlyName` 与计算的 `DEVPKEY_NAME` 均为原名。Capture 端点 Properties 的 DeviceDesc 为 `Microphone`、FriendlyName 覆盖仍缺失，字符串项未检出测试名；精确端点的 HKCU MMDevices 分支不存在，已知 HKCU Multimedia/Audio 分支未找到该端点或测试名引用。先前只检查 PnP FriendlyName，遗漏了独立 DeviceDesc，已将其纳入名称来源调查。
- [DEVPKEY_NAME 文档](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-name--device-instance-)说明其优先采用 FriendlyName，缺失时再采用 DeviceDesc；这解释了计算名称可为原名而另一属性仍是测试名。尚无证据确定现代声音设置读取哪一项；PnP DeviceDesc 残留和长驻设置进程缓存仍需区分，未将任一原因记为已确认，也未直接覆盖新发现的属性。

#### Windows 原生入口完成恢复与差异对照

- 用户将声音输入列表保留在前台后，CUA 取得有效目标截图：输入条目及 AB13X 属性页均显示 `Device Radar 测试麦克风`，适配器明确为 `AB13X USB Audio`。页面中的 Radar 与 PnP DeviceDesc 中的 Radiar 不同，不能把两者视为同一名称来源。
- 执行已授权的原名恢复前，封存新的 62 项端点基线 `bafde1683cbb4fcc9063a38128478d71.audio-snapshot.json`。此时 Core Audio 和原始 DeviceDesc 已是原名，PnP 三项名称为上段记录，设置进程保持原 PID 和启动时间。
- 在 Windows 设置的 AB13X 属性页点击 Rename，填写 `Microphone` 并提交。CUA 直接看到提交成功、属性页名称变为 `Microphone`；返回声音页面后，输入条目明确为 `Microphone` / `AB13X USB Audio`，选中状态保持。
- 09:50:11 UTC 独立探针继续确认 `observedState=original`、`resolvedState=original`、身份及 USB 祖先匹配。与刚才基线比较，62 项端点 Properties 的差异为零；09:50:12 UTC PnP DeviceDesc、FriendlyName、DEVPKEY_NAME 均为 `Microphone (AB13X USB Audio)`。SystemSettings PID 和启动时间未变，未通过重启设置进程或再次拔插实现此次同步。
- 本次原名恢复已由 Windows 原生入口收尾，并同时通过界面及程序回读。该结果不能计作探针定点写入流程的完整自动恢复通过：原生改名在端点持久值不变时更新了 PnP DeviceDesc 和当前设置显示，说明仍有更新步骤未被当前实现复现；具体 API、通知和各来源读取关系尚未确认。

#### v3 工具后端与无人值守验收

用户授权自行完成声音路径测试后，将音频写入替换为实验性的 `CPolicyConfigClient` / `IPolicyConfig.SetPropertyValue`，固定写当前端点主存储的 DeviceDesc。现代 ABI 含 `bFxStore=false` 参数；依据 [SoundSwitch 接口](https://github.com/Belphemur/SoundSwitch/blob/ee7def135eb96c7ac951e2d1535c7a661dfefbef/SoundSwitch.Audio.Manager/Interop/Interface/Policy/IPolicyConfig.cs)、[AudioDeviceCmdlets 接口](https://github.com/frgnca/AudioDeviceCmdlets/blob/26d08d30e9d804c016c994117f9b038b7d50da95/SOURCE/IPolicyConfig.cs)与 [Nodus 改名实现](https://github.com/voidless-labs/Nodus/blob/main/src-tauri/src/virtual_audio/endpoint_name.rs)。这是未公开接口，当前按 build 22631 限定，未宣称跨版本契约。

- 保留精确身份、USB 父链、原值、不可变恢复点和持久化 intent 守卫。音频普通权限运行；其他目标的管理员门槛保留。移除音频直接注册表写入旧路径。
- 回读同时比较原始字段、Core Audio Description/完整名/适配器名、PnP DeviceDesc/FriendlyName/DEVPKEY_NAME；分歧不计成功。原始值已到目标但其他来源仍为本次原名或拟写名时，可凭本次 apply 证据受守卫重调原生接口。第三方值、缺失来源及身份变化拒绝。
- 源码目录恢复点 `0e692e022ca04186b88f8c8bb8b32172.json`，v3 基线 `8cf2d5b4627946129486e55426bc9c62.audio-snapshot.json`。只读策略 Get 验证成功，`writeAttempted=false`。10:15:27 UTC 工具首次原生 apply 成功；各名称来源均为测试名，并收到 1 次同端点名称通知。后续 native restore/apply 对照均同步各来源。
- 10:26:41–10:27:22 UTC 的独立 WinRT DeviceWatcher 在工具 restore 时捕获两次目标 Updated，包含正确的 `System.ItemNameDisplay` 和 DeviceInterface FriendlyName。独立 FindAll/CreateFromIdFresh 的名称也正确，且 DeviceInstanceId 与绑定 PnP 实例完全对应。此证据将剩余显示差异定位到 Settings 对外部更名的界面缓存/事件消费。
- 已打开的设置进程可继续显示旧名，即使在前台或重新进入属性页。完整关闭该进程后，新进程直接显示正确名称。因此新增显式 `refresh-sound-settings --confirm-close`：正常关闭经固定映像、固定进程句柄和唯一窗口验证的 Settings，确认退出后重新打开声音页；超时或歧义拒绝，无确认参数时不做 UI 改动。此动作与设备写入分开，TUI 应在用户确认后提供。
- Release 构建无警告、无错误，60 项纯检查通过。静态复核通过接口布局、资源、恢复与权限守卫，以及设置刷新动作边界。自包含单文件程序已实际运行，无需本目录的运行时 DLL。

单文件程序的独立回归记录为 `248545aef8304b579a9ca9a72f5eb46c.json`，其 62 项基线为 `51549cd2b7eb42bbbd1d5af00461f3a7.audio-snapshot.json`，均在该 EXE 旁 data 目录：

| 检查 | 实际结果 |
| --- | --- |
| 10:38:01 UTC 原生 apply | 普通权限；全部名称来源为 proposed；系统名称通知 1 次 |
| 写入进程退出后的 WinRT 新进程核验 | FindAll 和 CreateFromIdFresh 的 Name、ItemNameDisplay、Interface FriendlyName 均为测试名，身份一致 |
| 原始属性差异 | 62 项中仅 DeviceDesc 改变，FriendlyName 覆盖仍不存在 |
| 工具刷新后的声音输入列表 | CUA 直接观察到 `Device Radar 测试麦克风` / `AB13X USB Audio` |
| 10:39:17 UTC 原生 restore | 全部来源为 original；系统名称通知 1 次 |
| 恢复后的独立 WinRT 与原始差异 | 各名称为原名；62 项相对本轮基线差异为零 |
| 工具刷新后的最终声音输入列表 | CUA 直接观察到 `Microphone` / `AB13X USB Audio` |
| 退出状态 | 探针进程数为零；当前原名保留 |

本轮没有在 Windows 设置中手工填写名称来帮助新后端通过。原生写入、通知、读回、设置刷新及恢复均由工具完成，CUA 仅负责观察与导航。此前的物理拔插/电脑重启存储验收属于旧写入路径对同一 DeviceDesc 的试验；新后端本轮没有再次执行这两项生命周期事件。Windows 10 和其他音频硬件尚未实测。

## 当前程序与继续核验

单文件实验包：[audio-native-v3](../artifacts/device-lab/audio-native-v3)，程序约 73.7 MB。记录、seal 和事件保存在程序旁按电脑分隔的 data 目录；[acceptance.json](../artifacts/device-lab/audio-native-v3/acceptance.json)记录文件 SHA-256、实际事件和验收边界。构建目录仍有对应探针，但其 data 与实验包分开。

当前为 v3 格式，v2 恢复点和快照作为历史证据保留，不交给新版加载。麦克风当前已恢复原名，无需再次写入。只读复核：

```powershell
$probe = '.\artifacts\device-lab\audio-native-v3\DeviceRadar.NativeProbe.exe'
& $probe verify --record '248545aef8304b579a9ca9a72f5eb46c.json'
& $probe diff-audio-baseline --snapshot '51549cd2b7eb42bbbd1d5af00461f3a7.audio-snapshot.json'
```

独立 WinRT 复核使用 [Verify-AudioInterface.ps1](../prototypes/DeviceRadar.NativeProbe/Verify-AudioInterface.ps1)，由 64 位 Windows PowerShell 5.1 运行，传实验包 EXE、上述 record token 和 `-ExpectedState original`。仅枚举和读回，唯一标识只输出短 hash。

在联合实验前，键盘三个缓存与 PnP 曾均回到原始状态。v2 PnP 记录 `7a04a7439d0342858bb665712bf4d01a.json`、缓存别名记录 `086bb848c7e84fa9ad138a40f691e290.json`、LEName 记录 `f0fbcb9eb388455e9f759e483c9af3af.json` 保留为历史；LEName 的回原值来自重启刷新，不能计作工具恢复验收。2026-10-27 的联合实验和当前恢复待办以 [BLE-JOINT-LAB](BLE-JOINT-LAB.md) 为准。
