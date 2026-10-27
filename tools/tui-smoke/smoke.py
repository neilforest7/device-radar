"""Read-only end-to-end checks against the real executable and real inventory."""
import argparse
import json
from pathlib import Path
import time

from controller import Controller
from wcwidth import wcswidth


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--exe", required=True, type=Path)
    p.add_argument("--output", required=True, type=Path)
    p.add_argument("--query", required=True, help="Local search text for a real device")
    args = p.parse_args()
    c = Controller(args.exe.resolve(), args.output.resolve(), 144, 24)
    steps = []

    def step(action, **values):
        command = {"action": action, **values}
        c.run(command)
        steps.append(command)

    def pause():
        time.sleep(0.5)

    def snap(name):
        pause()
        step("snapshot", name=name)
        return c.text()

    def require(condition, message):
        if not condition:
            raise AssertionError(message)
        print("PASS", message, flush=True)

    def click_label(screen, label):
        for y, line in enumerate(screen.splitlines(), 1):
            if label in line:
                x = wcswidth(line[:line.index(label)]) + 2
                step("mouse", x=x, y=y)
                return
        raise AssertionError("Button not found: " + label)

    def detail(screen):
        lines = screen.splitlines()
        title_index = next((i for i, line in enumerate(lines)
                            if "Device details" in line or "设备详情" in line), -1)
        if title_index < 0 or title_index + 1 >= len(lines):
            return ""
        cells = [cell.strip() for cell in lines[title_index + 1].split("││") if cell.strip()]
        return cells[-1] if cells else ""

    def text_has(screen, english, chinese):
        return english in screen or chinese in screen

    try:
        step("wait", contains="Device Radar", timeout=30)
        wide = snap("01-wide")
        lines = wide.splitlines()
        require("Devices (" in wide or "设备列表 (" in wide, "real inventory visible")
        require(("F3" in lines[1] and "F7" in lines[1]) and
                ("Devices (" in lines[3] or "设备列表 (" in lines[3]),
                "toolbar at row 2 and panels start at row 4")

        # The list owns initial focus; arrows change the selected device before / opens search.
        before = detail(wide)
        step("key", key="down")
        selected_screen = snap("02-arrow-selection")
        selected = detail(selected_screen)
        require(bool(selected) and selected != before, "initial focus is device list; arrows select another item")

        step("key", key="/")
        step("text", text=args.query)
        searched = snap("03-search")
        selected = detail(searched)
        require(args.query.casefold() in searched.casefold() and bool(selected), "slash search shows requested device")

        step("resize", cols=80, rows=24)
        step("sleep", seconds=2)
        narrow = snap("04-narrow")
        require(args.query.casefold() in narrow.casefold() and selected in narrow, "80x24 preserves query and selection")
        step("resize", cols=144, rows=24)
        step("sleep", seconds=2)
        wide_again = snap("05-wide-restored")
        require(args.query.casefold() in wide_again.casefold() and detail(wide_again) == selected,
                "wide resize preserves query and selection")
        step("key", key="f5")
        refreshed = snap("06-refreshed")
        require(args.query.casefold() in refreshed.casefold() and detail(refreshed) == selected,
                "F5 preserves query and selection")

        step("key", key="f6")
        changed = snap("07-language")
        require(("设备详情" in changed) != ("设备详情" in wide_again), "F6 switches language")
        step("key", key="f6")
        main = snap("08-language-restored")
        require(("设备详情" in main) == ("设备详情" in wide_again), "F6 restores language")

        # Tab enters the detail pane at Locations. Arrow navigation skips disabled actions.
        step("key", key="tab")
        step("key", key="enter")
        locations = snap("09-locations")
        require(text_has(locations, "All naming locations", "全部命名位置"), "Tab and Enter open Locations")
        step("key", key="escape")
        step("wait_not", contains="All naming locations" if "All naming locations" in locations else "全部命名位置",
             timeout=10)
        step("key", key="right")
        step("key", key="enter")
        next_action = snap("10-next-enabled-action")
        require(text_has(next_action, "History", "历史") or text_has(next_action, "Technical details", "技术详情"),
                "arrow navigation reaches the next enabled detail action")
        click_label(next_action, "[> Close <]" if "[> Close <]" in next_action else "[> 关闭 <]")
        step("key", key="tab")

        # For a device with no writable location, clicking the disabled Rename button must do nothing.
        if "0 writable" in main or "0 个可改名" in main:
            click_label(main, "[ Rename ]" if "[ Rename ]" in main else "[ 改名 ]")
            disabled_rename = snap("11-disabled-rename")
            require(not text_has(disabled_rename, "Current:", "当前："), "disabled Rename does not open editor")
        else:
            click_label(main, "[ Rename ]" if "[ Rename ]" in main else "[ 改名 ]")
            editor = snap("11-editor")
            require(text_has(editor, "Current:", "当前："), "enabled Rename opens editor")
            if text_has(editor, "Preview", "预览修改"):
                click_label(editor, "Preview" if "Preview" in editor else "预览修改")
                preview = snap("12-preview")
                require(text_has(preview, "Confirm to write", "确认后逐项写入"), "preview opens without writing")
                click_label(preview, "Back" if "Back" in preview else "返回")
                editor = snap("13-back")
                require(text_has(editor, "Current:", "当前："), "preview returns to editor")
            step("key", key="escape")
            step("wait_not", contains="Current:" if "Current:" in editor else "当前：", timeout=15)
            snap("14-cancel")

        # Append a non-matching suffix, then prove F3 guidance opens and Escape cancels.
        step("key", key="/")
        step("text", text="xyz-no-match")
        no_match = snap("15-no-match")
        step("key", key="f3")
        step("wait", contains="Disconnect the device you want to find"
             if "Search" in no_match else "断开要查找的设备", timeout=30)
        locate = snap("16-locate")
        require(text_has(locate, "Disconnect the device you want to find", "断开要查找的设备") and
                text_has(locate, "Locate device", "查找实物"), "F3 opens Locate guidance with no matching filter")
        step("key", key="escape")
        step("wait_not", contains="Disconnect the device you want to find"
             if "Disconnect the device you want to find" in locate else "断开要查找的设备", timeout=10)
        main = snap("17-locate-cancel")
        require(not text_has(main, "Disconnect the device you want to find", "断开要查找的设备"),
                "Escape cancels Locate without selecting a device")
        step("key", key="/")
        step("key", key="backspace", count=40)
        step("text", text=args.query)
        restored_query = snap("18-search-restored")
        require(args.query.casefold() in restored_query.casefold(), "cancelled Locate preserves search and selection")

        step("key", key="f7")
        settings = snap("19-settings")
        require(text_has(settings, "Windows settings", "Windows 设置"), "F7 opens Settings")
        step("key", key="escape")
        step("wait_not", contains="Windows settings" if "Windows settings" in settings else "Windows 设置",
             timeout=10)

        main = snap("20-before-elevation")
        if text_has(main, "Elevate F4", "管理员重开 F4"):
            step("key", key="f4")
            confirm = snap("21-elevation-confirm")
            require(text_has(confirm, "Restart as administrator", "管理员重开") and
                    text_has(confirm, "Request Windows administrator permission", "将请求 Windows 管理员权限"),
                    "F4 opens elevation confirmation")
            step("key", key="escape")
            step("wait_not", contains="Request Windows administrator permission"
                 if "Request Windows administrator permission" in confirm else "将请求 Windows 管理员权限",
                 timeout=10)
            cancelled = snap("22-elevation-cancel")
            require(not text_has(cancelled, "Request Windows administrator permission", "将请求 Windows 管理员权限"),
                    "Escape cancels elevation before any UAC request")
        else:
            print("SKIP F4: current process is already elevated", flush=True)

        step("key", key="escape")
        pause()
        if c.proc.isalive():
            step("key", key="escape")
            pause()
        require(not c.proc.isalive(), "Escape exits application")
    finally:
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / "steps.json").write_text(json.dumps(steps, ensure_ascii=False, indent=2), encoding="utf-8")
        c.close()


if __name__ == "__main__":
    main()
