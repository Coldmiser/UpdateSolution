# CapTG Automatic Update Solution

A two-program Windows solution that automatically applies Windows Updates, driver patches,
and winget application upgrades, then notifies the logged-in user when a reboot is required.

---

## Solution Structure

```
UpdateSolution/
├── Shared/                          # Class library — models, constants, helpers
│   ├── Models/
│   │   ├── PipeMessage.cs           # Wire-format for named pipe messages
│   │   ├── UpdateResult.cs          # Result of one update attempt
│   │   └── SnoozeOption.cs          # Snooze tier definitions
│   ├── Constants/
│   │   ├── AppConstants.cs          # Central string/value constants
│   │   └── RegistryConstants.cs     # Registry key/value names
│   └── Helpers/
│       └── RegistryHelper.cs        # Safe registry read/write
│
├── UpdateService/                   # Windows Service (runs as SYSTEM)
│   ├── Program.cs                   # Entry point; --install / --uninstall
│   ├── Logging/LogConfig.cs         # Serilog — two sinks (service + history)
│   ├── Install/ServiceInstaller.cs  # sc.exe wrapper with recovery options
│   ├── Service/UpdateBackgroundService.cs  # .NET BackgroundService host
│   ├── Workers/
│   │   ├── UpdateOrchestrator.cs    # Coordinates both workers; decides reboot
│   │   ├── WingetWorker.cs          # winget upgrade --all --include-unknown
│   │   └── WindowsUpdateWorker.cs   # PSWindowsUpdate module
│   ├── Pipes/PipeServer.cs          # Named pipe server; launches notifier
│   ├── Process/UserProcessLauncher.cs  # CreateProcessAsUser from SYSTEM
│   └── SelfUpdate/SelfUpdater.cs    # Remote version check + ZIP upgrade
│
└── UpdateNotifier/                  # WPF notification app (runs as logged-in user)
    ├── App.xaml / App.xaml.cs       # Entry point; pipe connect + window lifecycle
    ├── Logging/LogConfig.cs         # Serilog — notifier log
    ├── Pipes/PipeClient.cs          # Named pipe client
    ├── Snooze/SnoozeManager.cs      # Tracks and reduces available snooze tiers
    ├── ViewModels/MainViewModel.cs  # MVVM — binds to MainWindow
    ├── Views/
    │   ├── MainWindow.xaml          # Always-on-top, non-movable notification UI
    │   └── MainWindow.xaml.cs       # WndProc hook to block move/close/minimise
    └── Resources/
        └── CompanyLogo.png          # ← REPLACE with your actual logo
```

---

## Prerequisites

| Tool | Notes |
|------|-------|
| .NET 10 SDK | `dotnet --version` should show `10.x.x` |
| winget | Shipped with Windows 10 1809+ / Windows 11 |
| PowerShell 5.1+ | For PSWindowsUpdate; module auto-installed on first run |
| PSWriteColor module | Required by Build-And-Publish.ps1 — `Install-Module PSWriteColor -Scope CurrentUser` |
| Administrator rights | Required to install the service |

---

## First-Time Setup

### 1. Add your company logo

Copy your logo (PNG, ≥128×128 px) to:
```
UpdateNotifier\Resources\CompanyLogo.png
```

### 2. Configure URLs (optional)

The solution ships with placeholder update URLs.  Before deploying, either:

**Option A — Edit the constants** in `Shared/Constants/AppConstants.cs`:
```csharp
public const string DefaultVersionFileUrl = "https://your-server.com/version.txt";
public const string DefaultUpdateZipUrlTemplate = "https://your-server.com/releases/{0}/Update-{0}.zip";
```

**Option B — Set registry values** after installation:
```
HKLM\SOFTWARE\CapTG\UpdateService
  VersionFileUrl    REG_SZ   https://your-server.com/version.txt
  UpdateZipUrlTemplate REG_SZ https://your-server.com/releases/{0}/Update-{0}.zip
  LogDirectory      REG_SZ   C:\ProgramData\CapTG\Logs   (optional override)
```

### 3. Build & publish

```powershell
# From the solution root — run in an elevated terminal
.\Build-And-Publish.ps1
```

Both EXEs are output to `publish\Deploy\`.

### 4. Install the service

```powershell
# From an elevated prompt
.\publish\Deploy\UpdateService.exe --install
```

This registers the service with:
- Auto-start (delayed)
- Recovery: restart immediately → restart after 60 s → restart after 5 min
- Description visible in Services.msc

---

## Daily Operation

| Event | What happens |
|-------|-------------|
| Hourly (default) | Service runs Windows Update + winget, logs all results |
| Updates found + reboot needed | Service launches UpdateNotifier.exe as the active user |
| User selects "Reboot Now" | Service calls `shutdown.exe /r /t 30` |
| User snoozes | Service waits the selected duration, then relaunches the notifier |
| Each snooze | The longest remaining snooze option is permanently removed |
| Only 15 min + Reboot Now left | Those two options remain indefinitely |

---

## Log Files

All logs are written to `C:\ProgramData\CapTG\Logs\` (or the value in the registry):

| File | Contents |
|------|----------|
| `UpdateService-YYYYMMDD.log` | Full operational log (Verbose level) |
| `UpdateHistory-YYYYMMDD.log` | Audit trail: every KB/package attempt, status, timestamp |
| `UpdateNotifier-YYYYMMDD.log` | WPF notifier activity |

History logs are retained for 90 days; operational logs for 30 days.

---

## Self-Update

The service checks the remote version file once per hour (same cycle as updates).  
The version file must be plain text containing a single semantic version string, e.g.:

```
1.2.3
```

If the remote version is higher than `InformationalVersion` in the assembly,
the service downloads the ZIP, stages it, and runs a batch script that:
1. Stops the service
2. Robocopy-replaces the files
3. Restarts the service (or the SCM recovery policy does it)

**ZIP layout expected** (robocopy'd directly into the service directory):
```
CapTG-Update-1.2.3.zip
├── UpdateService.exe
└── UpdateNotifier.exe
```

---

## Uninstalling

```powershell
.\UpdateService.exe --uninstall
```

Registry values are intentionally preserved so that configuration survives a reinstall.

---

## Development Tips (VS Code)

Recommended extensions:
- **C# Dev Kit** (`ms-dotnettools.csdevkit`)
- **C#** (`ms-dotnettools.csharp`)

Launch configs (`launch.json`) — add two entries:

```json
{
  "name": "UpdateService (console)",
  "type": "dotnet",
  "request": "launch",
  "projectPath": "${workspaceFolder}/UpdateService/UpdateService.csproj"
},
{
  "name": "UpdateNotifier (WPF)",
  "type": "dotnet",
  "request": "launch",
  "projectPath": "${workspaceFolder}/UpdateNotifier/UpdateNotifier.csproj"
}
```

When debugging the WPF notifier standalone (no service running), you can temporarily
modify `App.xaml.cs` to load a mock `PipeMessage` instead of connecting to the pipe.

---

## Security Notes

- The named pipe uses default ACLs (accessible to all local users) — appropriate for a trusted corporate environment.
- The service token used to launch the notifier is obtained via `WTSQueryUserToken` / `CreateProcessAsUser`, which is the standard Session 0 isolation pattern.
- No credentials are stored; the service runs as `LocalSystem`.
