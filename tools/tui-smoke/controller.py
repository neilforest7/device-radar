"""Drive a real Windows console app via ConPTY; one JSON command per stdin line.

Install pywinpty and pyte into .tools/tui-test, then run with --exe and --output.
Screens and the raw terminal stream can contain device names; use an ignored output path.
"""
import argparse
import json
import os
from pathlib import Path
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / ".tools" / "tui-test"))
import pyte
from winpty import PtyProcess
from winpty.enums import Backend

KEYS = {
    "tab": "\t", "shift-tab": "\x1b[Z", "enter": "\r", "escape": "\x1b", "backspace": "\x7f", "/": "/",
    "up": "\x1b[A", "down": "\x1b[B", "left": "\x1b[D", "right": "\x1b[C",
    "f3": "\x1b[13~", "f4": "\x1b[14~", "f5": "\x1b[15~", "f6": "\x1b[17~", "f7": "\x1b[18~",
    "home": "\x1b[H", "end": "\x1b[F",
}


class Controller:
    def __init__(self, exe, output, cols, rows):
        self.output = output
        output.mkdir(parents=True, exist_ok=True)
        self.lock = threading.Lock()
        self.screen = pyte.Screen(cols, rows)
        self.stream = pyte.Stream(self.screen)
        self.raw = (output / "terminal.ansi").open("w", encoding="utf-8", newline="")
        # PtyProcess treats backend=0 as false, so force ConPTY via its environment setting.
        env = {**os.environ, "PYWINPTY_BACKEND": str(Backend.ConPTY)}
        self.proc = PtyProcess.spawn([str(exe)], cwd=str(exe.parent), env=env, dimensions=(rows, cols))
        self.thread = threading.Thread(target=self.read, daemon=True)
        self.thread.start()

    def read(self):
        try:
            while True:
                data = self.proc.read(16384)
                with self.lock:
                    self.raw.write(data)
                    self.raw.flush()
                    self.stream.feed(data)
        except (EOFError, OSError):
            return

    def text(self):
        with self.lock:
            return "\n".join(self.screen.display)

    def run(self, cmd):
        action = cmd["action"]
        if action == "key":
            self.proc.write(KEYS[cmd["key"]] * cmd.get("count", 1))
        elif action == "text":
            self.proc.write(cmd["text"])
        elif action == "mouse":
            x, y = int(cmd["x"]), int(cmd["y"])
            self.proc.write(f"\x1b[<0;{x};{y}M\x1b[<0;{x};{y}m")
        elif action == "resize":
            cols, rows = int(cmd["cols"]), int(cmd["rows"])
            with self.lock:
                self.screen.resize(lines=rows, columns=cols)
                self.proc.setwinsize(rows, cols)
        elif action in ("wait", "wait_not"):
            until = time.monotonic() + cmd.get("timeout", 10)
            while (cmd["contains"] in self.text()) != (action == "wait"):
                if not self.proc.isalive() or time.monotonic() >= until:
                    raise RuntimeError("Screen did not reach expected state")
                time.sleep(0.1)
        elif action == "sleep":
            time.sleep(cmd.get("seconds", 0.5))
        elif action == "snapshot":
            name = cmd["name"]
            if not name.replace("-", "").replace("_", "").isalnum():
                raise ValueError("Invalid snapshot name")
            text = self.text()
            (self.output / (name + ".txt")).write_text(text, encoding="utf-8")
            return {"text": text, "alive": self.proc.isalive()}
        elif action == "quit":
            return {"alive": self.proc.isalive()}
        else:
            raise ValueError("Unknown action")
        return {"alive": self.proc.isalive()}

    def close(self):
        self.proc.close(force=True)
        self.thread.join(timeout=1)
        self.raw.close()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cols", type=int, default=144)
    parser.add_argument("--rows", type=int, default=24)
    parser.add_argument("--commands", type=Path, help="Tail JSONL commands until quit or 30 minutes of inactivity")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")
    controller = Controller(args.exe.resolve(), args.output.resolve(), args.cols, args.rows)
    try:
        with (args.output / "steps.jsonl").open("w", encoding="utf-8") as steps:
            responses = (args.output / "responses.jsonl").open("w", encoding="utf-8") if args.commands else None

            def emit(result):
                line = json.dumps(result, ensure_ascii=False)
                if responses:
                    responses.write(line + "\n")
                    responses.flush()
                else:
                    print(line, flush=True)

            def dispatch(line):
                try:
                    cmd = json.loads(line)
                    result = {"ok": True, **controller.run(cmd)}
                    steps.write(json.dumps({"command": cmd, "result": result}, ensure_ascii=False) + "\n")
                    steps.flush()
                    emit(result)
                    return cmd["action"] == "quit"
                except Exception as exc:
                    result = {"ok": False, "error": str(exc)}
                    steps.write(json.dumps({"command": line.rstrip(), "result": result}, ensure_ascii=False) + "\n")
                    steps.flush()
                    emit(result)
                    return False

            try:
                emit({"ready": True, "pid": controller.proc.pid})
                if args.commands:
                    # Never execute an incomplete line; retain it until the writer adds a newline.
                    offset, pending = 0, b""
                    last_command = time.monotonic()
                    while time.monotonic() - last_command < 1800:
                        if args.commands.exists():
                            with args.commands.open("rb") as commands:
                                commands.seek(offset)
                                pending += commands.read()
                                offset = commands.tell()
                            while b"\n" in pending:
                                line, pending = pending.split(b"\n", 1)
                                last_command = time.monotonic()
                                if dispatch(line.decode("utf-8", errors="replace")):
                                    return
                        time.sleep(0.1)
                    emit({"ok": False, "error": "Command idle timeout"})
                else:
                    for line in sys.stdin:
                        if dispatch(line):
                            break
            finally:
                if responses:
                    responses.close()
    finally:
        controller.close()


if __name__ == "__main__":
    main()
