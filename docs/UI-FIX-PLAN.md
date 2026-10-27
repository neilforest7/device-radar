# 界面修复与验收

2026-10-28。生产入口 `src/DeviceRadar`；使用现有 Terminal.Gui 2.5 控件、字符网格、自检和 ConPTY，未使用 Computer-use。

| 问题 | 修复 |
| --- | --- |
| Tab 在搜索和筛选间循环 | 在应用键盘事件中处理 Tab / Shift+Tab，直接切换列表与详情；方向键在面板内选择。/ 进入搜索，↓ 进入筛选，列表顶端 ↑ 也可进入筛选；← → 选择、空格切换、↓ 回列表 |
| 详情与弹窗复杂 | 详情固定设备名、连接状态、位置与可写数量、操作栏；PgUp/PgDn 滚动。弹窗按位置、当前名、新名、原因等字段分行；设置操作纵排；按钮按实际宽度定位 |
| 提权后外观变化、线条错位 | 普通与提权启动使用同一初始化；驱动初始化后应用固定配色、禁用色及 ASCII 按钮括号。独占且可见的传统控制台使用 Consolas 并在退出恢复；窗口缩放使用 ScreenChanged 提供的新尺寸 |
| Locate 依赖已选设备 | F3 / 全局工具栏启动，不依赖搜索、筛选、选中项。记录全量在线基线，弹窗持续显示断开、等待重连、设备名称；完成后 Enter 清除遮挡结果的筛选并选中设备。等待阶段 Enter 不关闭；Esc 取消 |
| 提权依赖详情区域 | F4 / 全局工具栏；已是管理员时禁用入口；保留 Windows 授权确认流程 |
| F2 切换语言 | 改为 F6，同步使用说明与终端测试 |
| 不可用操作仍可点击 | 无设备时禁用详情操作；无位置时禁用位置入口及列表 Enter；无可写位置时禁用改名；无记录时禁用历史；编辑预览和历史恢复也按实际状态启用 |

历史可用性从每次枚举的持久记录快照匹配，搜索和选择不再逐次重读、回读全部历史。打开历史时重新读取，恢复预览仍执行原有实时身份、设备能力和记录验证；中断记录保留恢复预览路径。

## 验证边界

本次实际执行并通过：

- `./build.ps1`：Release 构建，0 warning / 0 error。
- `src/DeviceRadar/bin/Release/net10.0-windows/win-x64/DeviceRadar.exe --self-check`：33 项命名检查及 UI 自检，`systemWrites=0`。UI 包含 Tab/Shift+Tab、筛选键盘路径、详情翻页、空选择、Locate 断开/重连及 Enter 定位、搜索遮挡结果、重复重连事件拒绝，以及原有编辑和恢复检查。
- `py tools/tui-smoke/smoke.py --exe src/DeviceRadar/bin/Release/net10.0-windows/win-x64/DeviceRadar.exe --output artifacts/device-lab/ui-fixes/final-smoke --query AB13X`：ConPTY 19 项断言通过，含 80×24 / 144×24、F6、Tab/Enter、禁用改名按钮的真实鼠标输入、无匹配搜索时 Locate、设置、提权确认取消、退出。未确认 UAC 或系统改名。
- `--snapshot-dir artifacts/device-lab/ui-fixes/final-snapshots`、两个终端脚本的 `py_compile` 和 `git diff --check` 通过。

Locate 使用构造的清单变化验证完整定位状态；本次真实物理拔插与 UAC 新窗口字体仍需人工验收。终端渲染通过不能代替这两项。本机清单、设备名和终端原始证据保存在上述 Git 忽略目录。

框架行为按 [2.5.0 键盘文档](https://github.com/tui-cs/Terminal.Gui/blob/v2.5.0/docfx/docs/keyboard.md)与对应版本源码核对；字体设置范围按 [Windows 控制台 API](https://learn.microsoft.com/en-us/windows/console/setcurrentconsolefontex)限定。Windows Terminal 使用宿主配置的字体。
