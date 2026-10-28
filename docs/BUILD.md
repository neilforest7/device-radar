# Build and run

Windows x64 requires the .NET 10 SDK (10.0.100 or newer). Scripts use `.tools\dotnet\dotnet.exe` when present, otherwise `dotnet` on PATH. A published build includes the .NET runtime and does not require the SDK on the target machine.

This release was built with SDK 10.0.403. `src/DeviceRadar/packages.lock.json` records resolved dependency versions and content hashes. Use that SDK to reproduce the validated build; other SDK versions have not been tested in this run. `DeviceRadar.exe --file-check` separately runs isolated temporary filesystem checks, including locks, incomplete files and junction rejection; it never writes device properties.

```powershell
.\build.ps1
.\run.ps1
.\run.ps1 inventory
.\run.ps1 --self-check
.\build.ps1 -Action publish
.\build.ps1 -Action package
```

Publish writes `artifacts\release\v0.2.0-win-x64`, containing the single executable, documentation, licenses and `SHA256SUMS.txt`, plus a sibling ZIP and its SHA-256 file. It refuses to overwrite existing release outputs. `package` reuses the existing executable without rebuilding it, refreshes the public documentation and licenses, then replaces the checksums and ZIP. Both actions package only an explicit file list; local data and caches are excluded. Run `DeviceRadar.exe` from a writable directory; local settings and recovery points are stored beside the executable and should be kept private. Administrative elevation is requested inside the application when needed.

CLI output is JSON. `inventory` lists locally derived device and location tokens; use them with `capabilities --device <token>`, `history --device <token>`, and `prepare --device <token> --location <token> --name <name>`. `prepare` creates a local recovery point and returns a record token. `apply --record <token>`, `verify --record <token>`, and `restore --record <token>` use the same guards as the interface. Tokens and names may still identify local devices; treat output as private. `restore-preview --record <token>` previews restoring an older value; `restore-history --record <token>` applies that preview through a fresh recovery point. `update-identification` explicitly downloads the public ID databases. `--self-check` reads local inventory and settings and checks rendering without writing system properties or files. Exit codes are 0 for success, 1 for an error or unconfirmed result, and 2 for incorrect usage.

Naming capabilities are checked against the current device, location, platform, and permissions; no validation profile import is required. PnP and Classic Bluetooth writes require administrator permission. The current support and device-validation status are described in [SUPPORT](SUPPORT.md).
