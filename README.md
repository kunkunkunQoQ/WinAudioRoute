# WinAudioRoute

**Modern Windows audio control for .NET.**

- **Audio devices** — enumerate playback/recording endpoints in any state
- **Audio sessions** — session-granular enumeration, not just per-process
- **Volume / mute** — device-level and application-level
- **Default devices** — read and set, per flow and per role
- **Per-app input/output routing** — send one application's audio to a chosen device
- **Device / session events** — push-based change notifications
- **CLI + JSON** — `winaudio`, scriptable from PowerShell, Python, Node.js, AutoHotkey

```csharp
using WinAudioRoute;

var audio = new WindowsAudioManager();

var devices = audio.GetPlaybackDevices();
var sessions = audio.GetSessions();

audio.SetApplicationVolume(sessions[0].ProcessId, 0.5f);
audio.SetDefaultDevice(devices[0], AudioRole.Console);
```

```powershell
winaudio devices
winaudio sessions --json
winaudio volume chrome 35
winaudio route game.exe --output "Speakers"
```

---

## Why WinAudioRoute

Windows Core Audio is powerful but awkward to consume from .NET: WASAPI interop is manual,
audio *sessions* are not the same thing as *applications*, and the API that routes one application's
audio to a chosen device is not publicly documented at all.

WinAudioRoute packages that work into a small, documented, dependency-free SDK plus a JSON-capable CLI:

- **No UI, no tray, no framework.** The library is a plain class library with **zero third-party
  runtime dependencies**. It does not reference WPF, WinForms, or any UI framework.
  It also does **not** reference the WinRT projection: `Windows.Media.Internal.AudioPolicyConfig` is
  reached through direct `combase.dll` P/Invoke (`RoGetActivationFactory`, `WindowsCreateString`),
  so `WinRT.Runtime.dll` is never a dependency of your library code. (It may still appear in the
  output of an *executable* project, because it ships with the `net8.0-windows10.0.19041.0`
  framework reference itself — that is a property of the TFM, not of this package.)
- **Session-granular, not application-granular.** A process can own many sessions
  (one per browser tab, one per endpoint). The SDK returns sessions and lets you aggregate;
  it never silently collapses data.
- **Honest about failure.** No `bool`-and-swallow. Typed exceptions carry the original `HRESULT`,
  multi-target writes return per-target results, and unsupported features are reported through an
  explicit capability check instead of failing silently later.
- **Honest about undocumented APIs.** Per-app routing uses a Windows *internal* API.
  That is stated plainly, detected at runtime, and marked in JSON output — never described as official.
- **Machine-consumable.** Every CLI command supports `--json` on stdout with diagnostics on stderr,
  so PowerShell, Python, Node.js, and AutoHotkey can drive it.

---

## Features

| Area | What you get |
| --- | --- |
| **Devices** | Enumerate playback/recording devices in any state (active, disabled, not present, unplugged); friendly names; device states; per-role default flags |
| **Default devices** | Read and set the default device for `Console` / `Multimedia` / `Communications`, for both playback and recording |
| **Device volume** | Read/write device-level volume and mute (playback and recording endpoints) |
| **Sessions** | Enumerate audio sessions (session granularity), map session → PID → process name, read session state/identifiers |
| **Application volume/mute** | Read/write per-application volume and mute across *all* of a process's sessions, with per-session results |
| **Per-app routing** | Route one application's output or input to a specific device; read the current route; reset to "follow system default" |
| **Events** | `DeviceChanged` (added/removed/state/default/property) and `SessionChanged` (created/disconnected/state/volume/…) with event-driven cache invalidation |
| **CLI** | `devices`, `sessions`, `default`, `default-output`, `default-input`, `volume`, `mute`, `unmute`, `route`, `reset`, `capabilities`, with `--json` |

---

## Installation

```powershell
dotnet add package WinAudioRoute --version 0.1.0
```

Current stable release: **0.1.0**

```text
NuGet:          https://www.nuget.org/packages/WinAudioRoute
GitHub Release: https://github.com/kunkunkunQoQ/WinAudioRoute/releases/tag/v0.1.0
```

Target framework: `net8.0-windows10.0.19041.0`. The package declares its Windows platform
requirement, so the compiler warns if a project tries to use it on a non-Windows target.

Also published with this release: a symbol package (`.snupkg`) carrying the portable PDB and
SourceLink mapping, and prebuilt `winaudio` CLI archives for `win-x64` and `win-arm64`
(checksums in `SHA256SUMS.txt`), all on the release page linked above.

### Building from source

```powershell
git clone https://github.com/kunkunkunQoQ/WinAudioRoute.git
cd WinAudioRoute

dotnet build WinAudioRoute.sln -c Release

# The CLI lands in src/WinAudioRoute.Cli/bin/Release/net8.0-windows10.0.19041.0/winaudio.exe
.\src\WinAudioRoute.Cli\bin\Release\net8.0-windows10.0.19041.0\winaudio.exe capabilities

# Build a local package and consume it directly
dotnet pack src\WinAudioRoute\WinAudioRoute.csproj -c Release -o .\artifacts
# then, in your own project:
#   dotnet add package WinAudioRoute --source <path-to>\artifacts
```

Requirements: Windows 10 2004 (build 19041) or later, and the .NET 8 SDK.

---

## Quick Start

```csharp
using WinAudioRoute;

using var audio = new WindowsAudioManager();

// Devices
IReadOnlyList<AudioDevice> outputs = audio.GetPlaybackDevices();
IReadOnlyList<AudioDevice> inputs  = audio.GetRecordingDevices();
AudioDevice? defaultOutput = audio.GetDefaultDevice(AudioDataFlow.Render, AudioRole.Console);

foreach (AudioDevice device in outputs)
{
    Console.WriteLine($"{device.FriendlyName}");
    Console.WriteLine($"  id       = {device.Id}");
    Console.WriteLine($"  state    = {device.State}");
    Console.WriteLine($"  default  = {device.IsDefault}");
}

// Sessions (session granularity: the same PID can appear several times)
foreach (AudioSession session in audio.GetSessions())
{
    Console.WriteLine($"PID {session.ProcessId} ({session.ProcessName}) {session.State} vol={session.Volume}");
}
```

`WindowsAudioManager` is `IDisposable` because it owns COM references (session handles and the
native notification callback). Use `using`, or call `Dispose()` when your service shuts down.

Volume is a **scalar `float` in 0.0–1.0**. Out-of-range values are **clamped**, not thrown —
that is the documented contract. Percentage helpers (`*Percent` methods, `AudioVolume`) exist for
convenience.

---

## Device API

```csharp
// Enumerate. states accepts any combination of the four Windows device states.
var all     = audio.GetPlaybackDevices(AudioDeviceState.All);
var active  = audio.GetPlaybackDevices();              // Active only (default)
var inputs  = audio.GetDevices(AudioDataFlow.Capture, AudioDeviceState.Active);

// Default devices: six flow x role combinations.
AudioDevice? console  = audio.GetDefaultDevice(AudioDataFlow.Render,   AudioRole.Console);
AudioDevice? comms    = audio.GetDefaultDevice(AudioDataFlow.Capture,  AudioRole.Communications);

// Name/ID resolution: exact ID -> exact name -> unique substring (case-insensitive).
// Ambiguity throws AmbiguousAudioDeviceException with every candidate listed.
AudioDevice speakers = audio.ResolvePlaybackDevice("Speakers");
AudioDevice byId     = audio.ResolvePlaybackDevice("{0.0.0.00000000}.{...}");

// Change the system default device.
AudioOperationResult result = audio.SetDefaultDevice(speakers, AudioRole.Console);
if (!result.IsSuccess)
{
    Console.Error.WriteLine(result);   // Total/Succeeded/Failed + per-target HRESULTs
}
```

`AudioDeviceState.All` is `0x0F`, matching the official Windows constant
`DEVICE_STATEMASK_ALL = 0x0000000F`. Passing non-state bits (for example
`unchecked((int)0xFFFFFFFF)`) is normalized or rejected by the SDK.

---

## Session API

```csharp
IReadOnlyList<AudioSession> sessions = audio.GetSessions();               // everything
IReadOnlyList<AudioSession> playback = audio.GetSessions(AudioDataFlow.Render);
IReadOnlyList<AudioSession> ofPid    = audio.GetSessionsForProcess(1234); // by PID, may be empty

// By process name. "chrome" / "chrome.exe" / "CHROME.EXE" are equivalent.
// If several distinct PIDs share the name, this throws AmbiguousAudioSessionException
// (with the candidate PIDs) instead of picking one.
IReadOnlyList<AudioSession> ofChrome = audio.GetSessionsForProcess("chrome.exe");
```

A session exposes `State`, `Flow`, `DeviceId`, `SessionIdentifier`,
`SessionInstanceIdentifier`, `Volume`, `IsMuted`, and `IsSystemSoundsSession`.
When a session does not expose a volume interface, `Volume`/`IsMuted` are `null` rather than a fake value.

---

## Per-app Routing

```csharp
if (audio.IsPerAppRoutingSupported)
{
    AudioDevice target = audio.ResolvePlaybackDevice("SteelSeries Sonar - Gaming");

    // Route one application's output to a device.
    AudioOperationResult r = audio.SetApplicationOutput(processId, target);

    // Read the current route: null means "no persisted route" = follow system default.
    AudioDevice? current = audio.GetApplicationOutput(processId);

    // Input routing uses the same API with a capture device.
    audio.SetApplicationInput(processId, microphone);

    // Reset to "follow the system default device".
    audio.ResetApplicationOutput(processId);
    audio.ResetApplicationRouting(processId);   // both directions
}
```

### ⚠️ This feature uses an undocumented Windows API

**Read this before relying on it.**

- **What is used:** the WinRT internal class `Windows.Media.Internal.AudioPolicyConfig`,
  invoked through hand-written vtable calls with two interface IIDs (one for Windows 10, one for
  Windows 11 21H2+). Windows exposes no public API for per-application endpoint selection; this is
  the only known mechanism, and it is what other community tools use as well.
- **Is it guaranteed by Microsoft?** **No.** It is not a public, documented, or supported API.
  Microsoft has made no compatibility promise about its existence, its IIDs, its vtable layout, or
  its behaviour.
- **Tested on:** Windows 11 build 26100 (x64) only. See [TESTED_ENVIRONMENTS.md](docs/TESTED_ENVIRONMENTS.md).
- **What could break:** a Windows cumulative update could change the vtable layout or the IIDs.
  If that happens, routing may stop working or the call may fault.
- **How WinAudioRoute mitigates it:** availability is probed at runtime before any call
  (`IsPerAppRoutingSupported` / `RoutingCapability`), every write is verified by reading the route
  back, failures surface as `AudioRoutingNotSupportedException` (with the original `HRESULT`)
  rather than silent no-ops, and the CLI marks all routing JSON with `"usesUndocumentedApi": true`.
- **`null` means something specific:** passing `null` as the device deletes the application's
  persisted endpoint, i.e. "follow the system default device". This mirrors the underlying native
  semantics. Prefer the explicit `ResetApplicationOutput` / `ResetApplicationInput` methods if you
  do not like `null`.

---

## Events

```csharp
audio.DeviceChanged  += (_, e) => Console.WriteLine(e);   // Added/Removed/StateChanged/DefaultChanged/PropertyChanged
audio.SessionChanged += (_, e) => Console.WriteLine(e);   // Created/Disconnected/StateChanged/VolumeChanged/...

audio.NotificationHandlerFaulted += (source, ex) =>
    log.Warn($"{source} subscriber threw: {ex.Message}");
```

- Events are raised on **COM callback threads**. There is no UI-thread assumption; the library does
  not depend on a `Dispatcher` or `SynchronizationContext`. Marshal to your UI thread yourself.
- **Subscriber exceptions never cross the COM boundary.** They are isolated, and reported through
  `NotificationHandlerFaulted`. If they escaped, they would become unhandled native exceptions.
- Do not do heavy work in a handler (no full device/session enumeration, no routing writes, no blocking).
- Subscribing turns the event system on and registers the native session notifications. Device
  notifications are registered on construction because they drive cache invalidation; without them
  the caches fall back to a short TTL.
- The library adds **no timers and no background polling**. Events are push-based; TTL is only a fallback.

---

## CLI

```
winaudio devices [--output|--input] [--all] [--json]
winaudio sessions [--output|--input] [--json]
winaudio default [--output|--input] [--json]
winaudio default-output <device> [--role <role>] [--yes] [--json]
winaudio default-input  <device> [--role <role>] [--yes] [--json]
winaudio volume <process> <0-100> [--json]
winaudio mute <process> [--json]
winaudio unmute <process> [--json]
winaudio route <process> --output <device> [--json]
winaudio route <process> --input  <device> [--json]
winaudio reset <process> [--output|--input] [--json]
winaudio capabilities [--json]
winaudio --version
winaudio --help
```

**Diagnostics / testing helper:** `winaudio first-audible-pid [--json]` prints a PID that is safe to
modify for audio testing (it excludes system-critical processes and the calling process). It exists
for `scripts/test-real-audio.ps1` and is not part of the general-purpose API surface.

`<process>` is a PID or a process name (with or without `.exe`). `<device>` is matched by exact ID,
exact friendly name, then unique substring.

**stdout carries data, stderr carries diagnostics.** `--json` output is always on stdout, so it pipes safely.

**Ambiguity is an error, never a guess.** Matching several devices or several PIDs prints the
candidates to stderr and exits non-zero. Changing the system default device requires `--yes`,
because it is a global change that takes effect immediately.

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | General error |
| 2 | Usage / argument error |
| 3 | Device not found |
| 4 | Ambiguous device (candidates listed on stderr) |
| 5 | Session not found (or ambiguous process name) |
| 6 | Feature not supported on this system |
| 7 | Access denied |

---

## JSON API

Every command supports `--json`. Output is a stable envelope on stdout:

```json
{
  "tool": "winaudio",
  "version": "0.1.0",
  "command": "devices",
  "timestampUtc": "2026-01-01T00:00:00.0000000+00:00",
  "usesUndocumentedApi": false,
  "data": {
    "states": "Active",
    "devices": [
      {
        "id": "{0.0.0.00000000}.{...}",
        "name": "Speakers (Realtek)",
        "flow": "render",
        "state": "Active",
        "isActive": true,
        "isDefaultConsole": true,
        "isDefaultMultimedia": true,
        "isDefaultCommunications": false
      }
    ]
  }
}
```

`usesUndocumentedApi` is `true` for `route` and `reset`. Missing values are emitted as JSON `null`
rather than omitted, so consumers can distinguish "absent" from "not applicable".

### Consuming it

```powershell
# PowerShell
$devices = (winaudio devices --json | ConvertFrom-Json).data.devices
$devices | Where-Object isDefaultConsole | Select-Object name, id
```

```python
# Python
import json, subprocess
data = json.loads(subprocess.run(["winaudio", "sessions", "--json"],
                                 capture_output=True, text=True, check=True).stdout)
for s in data["data"]["sessions"]:
    print(s["processId"], s["processName"], s["volume"])
```

```javascript
// Node.js
const { execFileSync } = require("child_process");
const { data } = JSON.parse(execFileSync("winaudio", ["default", "--json"], { encoding: "utf8" }));
console.log(data.defaults);
```

```autohotkey
; AutoHotkey v2
RunWait A_ComSpec ' /c winaudio mute discord.exe', , "Hide"
```

---

## Architecture

```
WinAudioRoute.sln
├─ src/WinAudioRoute            ← the SDK (net8.0-windows10.0.19041.0, zero dependencies)
│  ├─ WindowsAudioManager.cs    ← the only public entry point
│  ├─ Devices/                  ← device enumeration + name/ID resolution
│  ├─ Sessions/                 ← process-name resolution
│  ├─ Routing/                  ← per-app routing backend + device-ID conversion + capability
│  ├─ Events/                   ← change event args + exception-isolating dispatcher
│  ├─ Models/                   ← immutable records, enums, results, exceptions
│  ├─ Components/               ← internal: COM services, ownership, caches, callbacks
│  ├─ Interop/                  ← internal: WASAPI declarations, P/Invoke, notification interfaces
│  └─ Platform/                 ← internal: architecture guard
├─ src/WinAudioRoute.Cli        ← the `winaudio` command (no third-party parser)
├─ tests/WinAudioRoute.Tests    ← unit + read-only integration + gated mutation tests
├─ samples/                     ← DeviceSwitcher, PerAppRouting, SessionMonitor
├─ scripts/                     ← test-real-audio.ps1 (real-hardware release gate)
└─ docs/                        ← COM reference ownership, cross-checks, tested environments
```

Key design points:

- **One public entry point.** `WindowsAudioManager` is the only type you need. Services,
  caches, COM ownership, backends, and interop declarations are `internal`.
- **Explicit COM ownership.** One owner per COM reference, released exactly once. The full
  audit — including every `QueryInterface`, `AddRef`, and `Release` — is in
  [COM_REFERENCE_OWNERSHIP.md](docs/COM_REFERENCE_OWNERSHIP.md).
- **Synchronous by design.** Core Audio and COM are synchronous. The library does not wrap them in
  `Task.Run` or offer a `CancellationToken` that cannot actually cancel anything. Schedule work
  yourself if you need it.
- **Capabilities are data, not assumptions.** `IsPerAppRoutingSupported` is a real probe
  (activation + interface validation), not a Windows-version guess.

### Platform support

| Target | Status |
| --- | --- |
| Windows 10 2004+ (build 19041+) x64 | Supported |
| Windows 11 x64 | Supported, primary development and verification platform |
| Windows 11 ARM64 | **Build verified; runtime not yet hardware-verified** (experimental) |
| x86 / 32-bit | **Not supported.** Explicitly rejected at construction |
| Non-Windows | **Not supported.** Explicitly rejected at construction |
| .NET Framework 4.8 | Not supported (planned for later evaluation) |

The library refuses to run in a 32-bit process because its interop layer assumes 64-bit struct
layouts; that assumption is asserted at startup and in tests, and failing fast is intentional.

---

## Supported Windows Versions

Declared minimum: `10.0.19041.0` (Windows 10 2004). See
[docs/TESTED_ENVIRONMENTS.md](docs/TESTED_ENVIRONMENTS.md) for what has actually been verified on
real hardware, capability by capability, and how to contribute a new environment record.

---

## Verification status

Everything stated here was executed; nothing is asserted from the code alone.

| Scope | Command | Result |
| --- | --- | --- |
| Build | `dotnet build -c Release` | 0 warnings / 0 errors |
| Unit tests (also the CI scope) | `dotnet test --filter "Category!=Integration&Category!=Mutation&Category!=Hardware&Category!=RealAudio"` | 293 passed / 0 failed |
| Integration tests (real hardware, read-only) | `dotnet test --filter "Category=Integration"` | 55 passed / 0 failed |
| Mutation tests (real state, restored) | `dotnet test --filter "Category=Mutation"` (needs `RUN_AUDIO_MUTATION_TESTS=1`) | 13 passed / 0 failed |
| Environment bypasses | — | **0** — every test exercised its assertions |
| x64 build | `dotnet build -r win-x64` | x64 (PE `0x8664`) |
| ARM64 build | `dotnet build -r win-arm64` | ARM64 (PE `0xAA64`) |
| NuGet contents | `eng/verify-package.ps1` | PASS |
| XML documentation | `eng/verify-xml-docs.ps1` + `XmlDocumentationTests` | PASS (608 documented members) |
| Package as a consumer | clean project referencing the packed `.nupkg` | build PASS, run PASS |

Reproduce the hardware-dependent rows with `pwsh ./scripts/test-real-audio.ps1` on a Windows machine
with audio endpoints. The script prints the OS build, architecture, device counts, per-app routing
availability, the per-suite pass/fail/**environment-bypass** counts, and then verifies that the
mutation suite restored the default device and per-app routing state.

> `env-bypassed = 0` is the number that matters. xUnit 2.5.3 has no `Assert.Skip`, so a test that
> cannot run returns early and is still reported as *passed* — the bypass count is how many tests
> did **not** exercise their assertions. Here it is zero.

**ARM64 build verified. Runtime not yet hardware-verified.**

---

## Known Limitations

1. **Per-app routing relies on an undocumented API.** See the warning in
   [Per-app Routing](#per-app-routing). There is no public alternative.
2. **Changing the system default device is a global side effect** and takes effect immediately for
   every application. The CLI therefore requires `--yes`.
3. **No global "reset all applications" API.** WinAudioRoute deliberately exposes only per-process
   resets. The underlying internal API can clear *every* application's persisted route at once (and
   the product it was extracted from additionally deletes a registry key to do so); that is a
   broad, hard-to-undo change, so it is not part of the public surface.
4. **ARM64 has not been hardware-verified.** It cross-compiles and that is all that is claimed.
5. **Device volume `Get` may return values slightly above 1.0** on some endpoints; the library clamps them.
6. **Application volume is aggregated**: reads return the first session of a process, writes apply to
   all of them. This is documented behaviour, not an accident.
7. **No async API yet.** If you need background execution, wrap calls yourself.

---

## Compatibility

- Built for **.NET 8**. The package targets `net8.0-windows10.0.19041.0`.
- Both the library and the CLI are compiled for `win-x64` and `win-arm64`.
- The public API is annotated with `[SupportedOSPlatform("windows10.0.19041")]`, so misuse from a
  cross-platform project is a compile-time warning.
- The package ships XML documentation for every public member, plus SourceLink and symbols.

---

## Security

- **No network access, no telemetry, no accounts, no cloud, no database.** The library makes no
  outbound connections of any kind.
- **No drivers, no hooks, no injection.** It uses documented WASAPI/MMDevice APIs plus the internal
  policy API described above.
- **No elevation required** for anything in the public API. Operations the current user is not
  permitted to perform fail with a typed exception carrying the `HRESULT` (`E_ACCESSDENIED` maps to
  exit code 7 in the CLI).
- **COM references are released deterministically**, including on the disposal paths, and event
  subscribers cannot crash the process through a callback.

---

## Contributing

Issues and pull requests are welcome.

```powershell
git clone https://github.com/kunkunkunQoQ/WinAudioRoute.git
cd WinAudioRoute

dotnet restore
dotnet build -c Release
dotnet test  -c Release --filter "Category!=Integration&Category!=Mutation&Category!=Hardware&Category!=RealAudio"
```

- The default test run covers unit tests only, so it works on any machine (including CI without audio hardware).
- Integration tests need real audio devices: `dotnet test -c Release --filter "Category=Integration"`.
- Mutation tests **modify real system state** and require an explicit opt-in:
  `$env:RUN_AUDIO_MUTATION_TESTS='1'`. They snapshot, modify, verify, and restore in `finally`.
- For a full real-hardware run use `scripts/test-real-audio.ps1`.
- Please add a record to [docs/TESTED_ENVIRONMENTS.md](docs/TESTED_ENVIRONMENTS.md) when you verify a
  new environment, including ARM64 hardware.

### CI scope

> CI verifies builds, unit tests and packaging.
> Hardware-dependent Windows audio integration tests are validated separately on real Windows systems.

The default GitHub Actions workflow runs **unit tests only**. It deliberately excludes
`Category=Integration` and `Category=Mutation` (and any future `Hardware` / `RealAudio` category),
because a hosted runner has no audio endpoints: those suites would mostly return early and then be
reported as passing, which is a false green. Real audio behaviour is verified on real machines via
`scripts/test-real-audio.ps1`, and `.github/workflows/hardware-tests.yml` is available as an opt-in,
manually triggered workflow for a self-hosted runner with audio devices.

---

## Origin

WinAudioRoute was extracted from audio infrastructure originally developed for
[SonicRoute](https://github.com/kunkunkunQoQ/SonicRoute).

The two projects are now maintained independently:

- **WinAudioRoute** is a reusable .NET library and CLI for Windows audio control.
- **SonicRoute** remains an independent desktop application with its own audio core.

They may share implementation lessons and fixes, but SonicRoute does not depend on WinAudioRoute.

---

## License

MIT. See [LICENSE](LICENSE).

WinAudioRoute's audio implementation was extracted from
[SonicRoute](https://github.com/kunkunkunQoQ/SonicRoute) (MIT, Copyright (c) 2026 kunkunkunQoQ).
Interop declarations are based on the public Windows SDK headers; the per-app routing approach was
informed by community implementations (EarTrumpet, SoundSwitch) without copying their code.
