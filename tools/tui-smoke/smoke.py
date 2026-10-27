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
        time.sleep(1)

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
                x = wcswidth(line[:line.index(label)]) + 1
                step("mouse", x=x, y=y)
                return
        raise AssertionError("Button not found: " + label)

    def detail(screen):
        parts = screen.splitlines()[2].split("│")
        return parts[4].strip() if len(parts) > 4 else ""

    try:
        step("wait", contains="Device Radar", timeout=30)
        wide = snap("01-wide")
        require("Devices (" in wide or "设备列表 (" in wide, "real inventory visible")
        before = detail(wide)
        step("mouse", x=8, y=6)
        until = time.monotonic() + 15
        while detail(c.text()) == before and time.monotonic() < until:
            time.sleep(0.1)
        after = snap("02-mouse")
        require(bool(detail(after)) and detail(after) != before, "mouse selects another list item")
        step("mouse", x=14, y=3)
        step("text", text=args.query)
        searched = snap("03-search")
        selected = detail(searched)
        require(args.query.casefold() in searched.casefold() and bool(selected), "search shows a device")
        step("resize", cols=80, rows=24)
        narrow = snap("04-narrow")
        require(args.query.casefold() in narrow.casefold() and selected in narrow, "80x24 preserves query and selection")
        step("resize", cols=144, rows=24)
        wide_again = snap("05-wide-restored")
        require(args.query.casefold() in wide_again.casefold() and detail(wide_again) == selected, "wide resize preserves query and selection")
        step("key", key="f5")
        refreshed = snap("06-refreshed")
        require(args.query.casefold() in refreshed.casefold() and detail(refreshed) == selected, "F5 preserves query and selection")
        step("key", key="f2")
        changed = snap("07-language")
        require(("设备详情" in changed) != ("设备详情" in wide_again), "F2 switches language")
        step("key", key="f2")
        initial = snap("08-language-restored")
        require(("设备详情" in initial) == ("设备详情" in wide_again), "F2 restores language")
        step("key", key="tab", count=5)
        step("key", key="enter")
        locations = snap("09-enter")
        require("All naming locations" in locations or "全部命名位置" in locations, "Tab and Enter open device details")
        step("key", key="escape")
        step("wait_not", contains="All naming locations" if "All naming locations" in locations else "全部命名位置", timeout=15)
        main = snap("10-escape")
        require("All naming locations" not in main and "全部命名位置" not in main, "Escape closes device details")
        click_label(main, "Rename" if "Rename" in main else "改名")
        editor = snap("11-editor")
        require("Current:" in editor or "当前：" in editor, "rename editor opens")
        editor_rows = [line.split("│")[3] for line in editor.splitlines()[3:17] if len(line.split("│")) > 3]
        if "☑" in "\n".join(editor_rows):
            click_label(editor, "Preview" if "Preview" in editor else "预览修改")
            preview = snap("12-preview")
            require("Confirm to write" in preview or "确认后逐项写入" in preview, "preview opens without writing")
            click_label(preview, "Back" if "Back" in preview else "返回")
            editor = snap("13-back")
            require("Current:" in editor or "当前：" in editor, "preview returns to editor")
        else:
            print("SKIP preview: selected device has no visible writable location", flush=True)
        step("key", key="escape")
        step("wait_not", contains="Current:" if "Current:" in editor else "当前：", timeout=15)
        closed = snap("14-cancel")
        require("Current:" not in closed and "当前：" not in closed, "escape cancels editor")
        step("key", key="escape")
        pause()
        require(not c.proc.isalive(), "escape exits application")
    finally:
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / "steps.json").write_text(json.dumps(steps, ensure_ascii=False, indent=2), encoding="utf-8")
        c.close()


if __name__ == "__main__":
    main()
