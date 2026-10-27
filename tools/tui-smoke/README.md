# Real terminal checks

Development tools only; Windows ConPTY runs the actual EXE against local inventory. Raw terminal output and screenshots can contain local names. Save output under the ignored `artifacts/device-lab` directory.

```powershell
py -m pip install --target .tools/tui-test pywinpty pyte
py tools/tui-smoke/smoke.py --exe artifacts/release/v0.1.0-win-x64/DeviceRadar.exe --output artifacts/device-lab/v1/smoke --query AB13X
```

Choose a query for a real connected device. Smoke checks enumeration, mouse, keyboard, language, resize, selection, preview and cancellation; it never confirms a device write. The interactive `controller.py` accepts JSON lines on stdin, or tails `--commands <file>` and writes `responses.jsonl`. Supported actions are `key`, `text`, `mouse`, `resize`, `wait`, `wait_not`, `sleep`, `snapshot`, and `quit`. It never interprets shell commands. File mode stops after 30 minutes without a command; quitting closes only its own child process.

Actual device-write acceptance requires the explicit device authorization, identity checks, durable records and restoration used by the product. A successful render or smoke check does not prove a rename, Windows UI display or lifecycle persistence.
