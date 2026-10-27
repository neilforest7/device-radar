# Native naming probe

最小原生实验 CLI，限定五个目标 token：

- `mouse`：唯一在线 Mouse 类 `HID\VID_046D&PID_C547...` 节点的 PnP FriendlyName。Windows build 22631 已验证设备管理器显示、进程退出、同口拔插、重启保留及恢复，见[实测记录](../../docs/MOUSE-PERSISTENCE-LAB.md)。
- `ble-keyboard`：当前 VGN N75 PRO-1 的 BTHLE 根节点，实验写入其精确映射的 BTHPORT 缓存 `FriendlyName`。仅支持 build 22631；BLE 配对列表、设置界面与持久性尚未验证。
- `ble-keyboard-pnp`：同一键盘的独立 PnP 根 FriendlyName 实验，使用 SetupAPI、原始类型 `18`；单独准备恢复点。仍核对 BTHLE 根、相同地址硬件 ID、ContainerId 与缓存 Name/LEName，不写缓存别名。配对列表和设置名称效果待验收。
- `ble-le-name-cache`：同一键盘的 LEName 单字段来源实验。只写现存 BTHPORT `LEName` REG_BINARY、UTF-8 加 NUL；Name 工厂值与 FriendlyName 单字节 `00` 固定为守卫。额外要求同容器现存 HIDClass transport 的完整公开硬件 ID 为 `BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02320f_PID&5055_REV&0104`。此来源缓存实验不构成持久化后端验收。
- `usb-microphone`：AB13X USB Audio，VID/PID `001F:0B21`，唯一在线、ACTIVE 且三个角色均为默认的 Capture 端点。仅在 build 22631 实验调用原生 `IPolicyConfig.SetPropertyValue`，固定主存储 DeviceDesc（pid 2），普通权限运行。原始 Properties `REG_SZ` 仅用于独立读回；原生 Settings 显示与持久性分别验收。

仅使用 .NET 内置功能、Windows SetupAPI、Core Audio、实验音频策略 COM 接口和固定注册表路径，目标 `net10.0-windows` / `win-x64`。自包含单文件发布，无服务、任务或驻留程序。

```powershell
# 项目根目录；只构建，不运行系统写入
$env:DOTNET_CLI_HOME = Join-Path $PWD '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD '.tools\nuget'
& .\.tools\dotnet\dotnet.exe build .\prototypes\DeviceRadar.NativeProbe\DeviceRadar.NativeProbe.csproj -c Release

# 发布目标由调用者选择。EXE及旁边data目录作为一个实验包保管。
& .\.tools\dotnet\dotnet.exe publish .\prototypes\DeviceRadar.NativeProbe\DeviceRadar.NativeProbe.csproj -c Release -o .\artifacts\device-lab\native-probe
```

在 EXE 所在目录运行：

```powershell
.\DeviceRadar.NativeProbe.exe --self-check
.\DeviceRadar.NativeProbe.exe inspect --target mouse
.\DeviceRadar.NativeProbe.exe inspect --target ble-keyboard
.\DeviceRadar.NativeProbe.exe inspect --target ble-keyboard-pnp
.\DeviceRadar.NativeProbe.exe inspect --target ble-le-name-cache
.\DeviceRadar.NativeProbe.exe inspect --target usb-microphone
.\DeviceRadar.NativeProbe.exe prepare --target mouse --name 'Device Radar 测试鼠标'
.\DeviceRadar.NativeProbe.exe prepare --target ble-keyboard --name 'Device Radar 测试键盘'
.\DeviceRadar.NativeProbe.exe prepare --target ble-keyboard-pnp --name 'Device Radar 测试键盘'
.\DeviceRadar.NativeProbe.exe capture-ble-baseline
.\DeviceRadar.NativeProbe.exe prepare --target ble-le-name-cache --name 'Device Radar 测试键盘'
.\DeviceRadar.NativeProbe.exe prepare --target usb-microphone --name 'Device Radar 测试麦克风'

# prepare返回record文件名。把下方占位值换为该文件名。
# usb-microphone apply/restore 使用普通终端；其他目标要求管理员。程序自身不请求UAC。
.\DeviceRadar.NativeProbe.exe apply --record '<operation-id>.json'
.\DeviceRadar.NativeProbe.exe verify --record '<operation-id>.json'
.\DeviceRadar.NativeProbe.exe restore --record '<operation-id>.json'

# 只读能力检查：音频调用策略 GetPropertyValue；BLE 打开最小权限句柄。均不调用 Set。
.\DeviceRadar.NativeProbe.exe probe-write-access --record '<operation-id>.json'

# 音频原生界面实验：改名前捕获全部 Properties 原始类型和字节；改名后只读比较。
.\DeviceRadar.NativeProbe.exe capture-audio-baseline
.\DeviceRadar.NativeProbe.exe diff-audio-baseline --snapshot '<operation-id>.audio-snapshot.json'

# 确认“将关闭并重新打开 Windows 设置”后，显式刷新声音页面。
.\DeviceRadar.NativeProbe.exe refresh-sound-settings --confirm-close
```

`inspect` 只读系统。`prepare` 只读系统并创建本地恢复点。`verify` 在独立枚举中只读系统，把观测另存结果文件。`--self-check` 纯内存，文件和系统属性写入次数均为零。退出码 `0` 表示本命令成功；`1` 表示拒绝、失败或无法确认；`2` 表示用法错误。`verify` 的 `original` 也返回 `0`，必须结合 `observedState` 判定。

输出单行精简 JSON。公开目标短 hash、类、公开 VID/PID、名称和原始属性存在状态；完整实例 ID、硬件 ID、ContainerId 与原始字节仅存本地恢复点。本机指纹来自 MachineGuid 的 SHA-256，不在输出中展示。`observedState` 是原始存储字节状态；`resolvedState` 是重新查询的名称状态。音频额外输出 Core Audio Description/InterfaceName、PnP DeviceDesc/FriendlyName/DEVPKEY_NAME 及各来源状态，`synchronizedState` 仅在全部来源对应同一恢复点状态时为 `original` 或 `proposed`，分歧为 `mixed`，外部值或缺失为 `other`。原名与拟写名相同时使用 `original-and-proposed`。`notificationCount` 为当前端点名称回调数，仅作观测。BLE 的 `systemName` 是 PnP 根名称，配对列表名称需独立观察。

Windows Settings 进程可能缓存声音名称；原生写入、全部来源同步以及名称回调均不能证明当前页面已重绘。`refresh-sound-settings` 无确认参数时返回 `confirmation-required`，不改变界面。用户确认“将关闭并重新打开 Windows 设置”后，调用 `refresh-sound-settings --confirm-close`：只定位固定 Windows `ImmersiveControlPanel\SystemSettings.exe`，必须最多一个进程及唯一可见根窗口；窗口可直接属于该进程，或包含其子窗口。重新核对绑定后发送 `WM_CLOSE`，等待退出最多 5 秒，再请求打开固定 `ms-settings:sound`。未运行时直接打开；歧义、权限失败或未退出均不重新启动。输出 `closedCount`、`launched`、`refreshState`；`sound-page-launch-requested` 表示系统接受打开请求，显示内容仍需界面验收。此动作由用户显式选择，apply/restore 不自动调用。

独立只读验收可在 Windows PowerShell 5.1 x64 运行同目录 `Verify-AudioInterface.ps1`，传入 `-ProbePath <exe> -RecordToken <token> -ExpectedState original|proposed`；它先调用 probe verify，再以新的 WinRT 查询核对名称与绑定身份。

当前恢复点和事件格式为 v3，明确绑定目标 token 和固定命名位置，不迁移或加载旧格式；已有 v2 文件保留为历史记录。音频 `.audio-snapshot.json` 及其 seal 与恢复点共用同一目录和安全文件存储；仅供差异观察，不能作为 `apply`/`restore` 的输入。差异输出只公开属性 key、类型、长度和短 hash；已知名称属性可显示文本。

`capture-ble-baseline` 同样只读系统，将三项缓存 Name、LEName、FriendlyName 的原始类型/字节，以及 PnP FriendlyName 原始值、根身份、解析名封存为 `.ble-snapshot.json` 和 SHA-256 seal。它与恢复点复用安全文件存储，不提供批量恢复。AEP 名由外部只读 sampler 独立观察；配对 selector 的实际 Kind 是 `AssociationEndpoint`。AEP 名与 Windows Settings 显示名需要分别验收。

本地数据位于 EXE 旁 `data/<本机指纹>/`，已通过本目录 `.gitignore` 排除。`--record` 接受文件名 token，或该目录内恢复点的完整/相对文件路径；拒绝路径逃逸、数据目录祖先/记录文件/事件目录中的 reparse point。每份 `<operation-id>.json` 恢复点不可覆盖；尝试及结果分别存入 `<operation-id>.events/`。每份 JSON 先以 `CreateNew` 创建、`Flush(true)`、同句柄回读，再创建并持久化 64 字节 SHA-256 `.sha256` 文件作为提交标记。缺失或不完整提交标记的恢复点拒绝加载，相应事件忽略；完整标记与内容不符则拒绝。输出 `result` 是事件目录与文件名 token，便于提权子进程退出后检查结果。恢复依赖 JSON、对应 `.sha256` 与事件目录，应一并保管。

写入边界：

- 仅 `usb-microphone` 的 `apply` 和 `restore` 免管理员要求。每次重新枚举，候选必须恰好一个。核对本机指纹、精确实例 ID、类、硬件 ID、ContainerId；音频还核对 MEDIA 与 USB Composite 两级父设备身份、USB 硬件族、`usbaudio`/`usbccgp` 服务及默认角色。
- 鼠标唯一写入目标是 `DEVPKEY_Device_FriendlyName`：`{a45c254e-df1c-4efd-8020-67d146a850e0},14`。BLE 唯一写入目标是从当前根实例 `DEV_<12hex>` 推导的现存 BTHPORT 缓存 key 内现存 `FriendlyName` REG_BINARY。音频策略接口只对已绑定 Capture 端点调用主存储（`bFxStore=false`）的 `{a45c254e-df1c-4efd-8020-67d146a850e0},2`。记录不提供可执行命令、注册表路径或可变属性 key。
- BLE 必须有与根地址相等的硬件 ID、非空 ContainerId；缓存 `Name` 与 `LEName` 必须仍为精确的 `VGN N75 PRO-1\0` UTF-8 字节。拟写别名为 UTF-8 加 NUL，原始 `00` 按 REG_BINARY 单字节恢复。仅打开现存 key/value，不修改 Name、LEName、Enum 属性或 ACL，不重启或断开设备。
- BLE PnP 实验仅调用固定 `DEVPKEY_Device_FriendlyName` 的 SetupAPI 写入。不存在原属性时恢复为不存在；缓存 Name/LEName 只读，BTHPORT FriendlyName 不写。它与缓存别名属于不同目标 token 和恢复点。
- LEName 实验定位只依赖精确 BTHLE 根身份、地址硬件 ID、ContainerId 及同容器 HID 型号，不以可变 LEName 或 PnP FriendlyName 筛选。写入和恢复前要求 Name 仍为 `VGN N75 PRO-1\0`、FriendlyName 仍为 REG_BINARY 单字节 `00`；偏离分别报告 `ble-cache-coupling-name-changed`、`ble-cache-coupling-friendly-name-changed` 并拒绝写入，不自动恢复这些旁路字段。仅现存 LEName 允许变化，恢复使用其封存原始字节。
- LEName 的在线值、封存原值和拟写值必须为严格 UTF-8 加单个终止 NUL，不接受无终止 NUL、嵌入 NUL 或非法 UTF-8；校验不转换或规范化原始字节。缓存别名 FriendlyName 的单字节 `00` 继续采用独立契约。
- `apply` 要求当前原始类型和字节等于恢复点；`restore` 要求当前值精确等于拟写值，并有匹配恢复点 hash 的成功回读结果或已持久化 apply intent。intent 包含原值快照，允许写入后进程中断时恢复。仅名称相同不足以恢复。当前已经是原值则恢复为无操作。外部改名、身份变化、离线、歧义都拒绝写入。
- 鼠标原属性不存在时，恢复调用 `DEVPROP_TYPE_EMPTY`、空指针、长度 `0` 删除属性。不会把 DeviceDesc 作为原 FriendlyName 写入。
- 音频 `prepare` 要求现存、有效且非空的 DeviceDesc `REG_SZ`，其基础名称、Core Audio Description、完整解析名和三项 PnP 完整名称全部一致；不一致则报告 `audio-baseline-inconsistent`。解析名称后缀取自只读 `PKEY_DeviceInterface_FriendlyName`，始终必须与恢复点一致。音频写入前再次核对原始值，并通过策略 Get 复核；只在持久化 intent 和独立枚举守卫之后调用策略 Set。
- 音频原始值已到目标但其余来源仍为恢复点原名或拟写名时，仅凭匹配 apply 证据允许再次调用原生 setter 促成同步。全部来源已同步才是 NoOp；任何外部第三个名称、缺失来源或后缀变化均拒绝。恢复到已同步原名可直接 NoOp。音频写入后独立重读各来源，最多轮询 5 秒等待传播；API 失败也执行独立读回。
- 注册表通过原生 `RegOpenKeyExW` 打开 HKLM 固定现存 key，读仅申请 `KEY_QUERY_VALUE`，写仅额外申请 `KEY_SET_VALUE`，明确使用 64 位视图。打开写句柄和同句柄现值复核在实际 Set 前完成；打开失败的 `writeAttempted` 为 `false`。恢复点与 intent 仍须先持久化。事件仅记录异常类型、HResult、实际原生错误码，不记录异常消息或堆栈；`SecurityException` 本身不被臆断为 Win32 错误 `5`。
- `.mutation.lock` 以独占文件锁覆盖核验、写入、回读和结果落盘。目录从卷根逐级以 `OPEN_REPARSE_POINT | BACKUP_SEMANTICS` 打开并检查句柄属性，只共享读取，全程阻止目录删除、改名及写访问。每次写入前，恢复点和 intent 都必须完成提交；其 JSON 与 `.sha256` 句柄也保持无写入/删除共享，直到回读结束。再次检查现值后才调用写入 API。
- API 返回失败后仍重新只读枚举回读。`value-confirmed-api-error` 表示回读字节正确但 API 报错；退出码仍为 `1`，保留两项事实。`write-unconfirmed` 和 `result-log-failed` 需要检查日志及独立 `verify`，不可当作成功。
- BLE 原始字节回读匹配时返回 `storage-confirmed-ui-unverified`、退出码 `1`；这只确认缓存存储，不能证明配对列表、设置界面或重启后名称。音频原始存储正确但任一名称来源未同步时，返回 `raw-confirmed-sources-stale`、退出码 `1`。音频成功与 `verify` 的退出码 `0` 要求全部来源同步；真实 Settings 界面仍由原生 UI 独立验收。
- BLE PnP 原始属性回读匹配时返回 `pnp-property-confirmed-paired-name-unverified`、退出码 `1`；需要独立观察配对名称。初次检查已到目标值的 NoOp 也必须通过最终独立观测；离线、其他值或不可用为 `observation-unconfirmed`、退出码 `1`。
- LEName 原始字节回读匹配时返回 `source-cache-confirmed-ui-unverified`、退出码 `1`。Name/LEName 优先级没有公开契约；缓存、PnP、AEP、Settings 和正常重连/重启结果应分别记录。代码不触发断开、复位、刷新或重启。

SetupAPI、注册表和实验音频策略接口没有这里可用的原子 compare-and-set 契约：检查现值和写入之间，外部程序仍可能同时改名。本实验应避免同时使用其他改名工具。输入允许 Unicode，拒绝空白名称、控制字符、NUL、非法代理字符和超过 256 个 UTF-16 单元的名称。Core Audio 基本端点 `IPropertyStore` 始终以只读方式打开；音频改名依赖未公开、限定 build 22631 的策略接口，不能据此宣称跨版本受支持。

正式接口依据：[FriendlyName 类型及读写接口](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-friendlyname)、[设置属性及管理员要求](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdisetdevicepropertyw)、[删除不存在/现存属性的契约](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devprop-type-empty)、[读取属性和缓冲区长度](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdevicepropertyw)、[SP_DEVINFO_DATA 的 cbSize](https://learn.microsoft.com/en-us/windows/win32/api/setupapi/ns-setupapi-sp_devinfo_data)。
