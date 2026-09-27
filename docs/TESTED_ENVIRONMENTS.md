# TESTED_ENVIRONMENTS.md

Real-hardware verification records for WinAudioRoute.

This file exists because **a green CI run does not prove real Windows audio behaviour.**
CI verifies builds, unit tests, and packaging on a runner that usually has no audio endpoints.
Anything listed below as *verified* was exercised on a real machine with real devices.

Adding a new record is a welcome contribution — see [Contributing](#contributing).

---

## Legend

| Status | Meaning |
| --- | --- |
| **Verified** | Exercised on real hardware with real devices; the listed capability behaved correctly |
| **Partially verified** | Exercised, but only with the limitations noted in the row |
| **Build verified only** | Compiles and packages; **no runtime verification on this platform** |
| **Not verified** | Never exercised |

---

## Record 1 — Windows 11 24H2 (x64)

| Field | Value |
| --- | --- |
| OS | Windows 11 专业版 (Pro) |
| Build | **26100** (24H2) |
| Architecture | **x64** |
| .NET SDK | 8.0.424 |
| Runtime | .NET 8.0.24 / 8.0.30 |
| Library version | 0.1.0 (Milestone B.1 / C) |
| Verified by | project maintainer |
| Verified date | Milestone B.1 / C |

### Capability matrix

| Capability | Status | Notes |
| --- | --- | --- |
| Device enumeration (playback) | **Verified** | 9 active playback endpoints observed |
| Device enumeration (recording) | **Verified** | 6 active capture endpoints observed |
| Non-active device enumeration | **Verified** | `AudioDeviceState.All` returned 39 render endpoints (disabled / not present / unplugged combined) |
| Device state reporting | **Verified** | `Unplugged` and `Disabled` states observed and reported correctly |
| Device friendly names | **Verified** | Includes non-ASCII names (e.g. Chinese device names) |
| Default device read — all six flow/role combinations | **Verified** | Render and Capture × Console/Multimedia/Communications all returned `S_OK`; Communications differed from Console/Multimedia on this machine, confirming real role separation |
| System default device write | **Verified** | Written and read back with `MATCH`; original state restored afterwards |
| Device volume read/write | **Verified** | Scalar read, write, clamping at 0.0 and 1.0, value restored |
| Device mute read/write | **Verified** | Toggled and restored |
| Session enumeration | **Verified** | Session granularity confirmed: one PID appeared with 10 distinct sessions |
| Session → process mapping | **Verified** | PID and process name both resolved |
| Session state / identifiers | **Verified** | `SessionIdentifier` and `SessionInstanceIdentifier` populated and unique |
| Application volume read/write | **Verified** | Written to all sessions of the process, read back, restored |
| Application mute read/write | **Verified** | Toggled and restored |
| Application name resolution (`chrome` / `chrome.exe` / case) | **Verified** | Unit-tested; ambiguity path returns candidates |
| **Per-app output routing** | **Verified** | Real write + read-back + restore of a persisted output route |
| **Per-app input routing** | **Verified** | Real write + read-back + restore of a persisted input route |
| Per-app routing reset (follow system default) | **Verified** | `null` semantics and explicit reset both verified |
| `IsPerAppRoutingSupported` | **Verified** | Probed as `supported` on this build |
| `IMMNotificationClient` registration | **Verified** | Registered successfully; unregistered on dispose |
| Device event delivery | **Partially verified** | Registration, mapping, and callback isolation are unit-tested and registration succeeds on hardware. **Real device add/remove events were not forced** (that needs physical hardware changes) |
| Session notifications (`IAudioSessionNotification`) | **Verified** | Registered successfully on the default render endpoint |
| Session events (`IAudioSessionEvents`) | **Partially verified** | Registered per session; mapping and disconnect handling unit-tested. **Real session create/destroy events were not forced** |
| Event-driven cache invalidation | **Verified** | Invalidation path exercised; repeated enumeration does not accumulate COM handles |
| Routing mutation + full restore | **Verified** | Snapshot → modify → verify → restore; before/after state comparison identical |
| Volume/mute mutation + full restore | **Verified** | Default devices and routes identical before and after |
| 64-bit guard | **Verified** | Construction rejects 32-bit processes before any native call |
| x64 build | **Verified** | PE machine `x64` |

### Per-app routing on this build

`Windows.Media.Internal.AudioPolicyConfig` was available and functional. As with any undocumented
interface, this is a statement about **build 26100**, not a guarantee.

---

## Record 2 — Windows 11 (ARM64)

| Field | Value |
| --- | --- |
| OS | Windows 11 |
| Architecture | **ARM64** |
| Library version | 0.1.0 |
| Status | **Build verified only** |

| Capability | Status |
| --- | --- |
| Cross-compilation (`-r win-arm64`) | **Verified** — produces an ARM64 assembly (PE machine `0xAA64`) |
| Packaging for ARM64 | **Verified** |
| **Any runtime behaviour** | **Not verified — no ARM64 hardware available** |

> **ARM64: Build verified. Runtime not yet hardware-verified.**
>
> This must not be described as fully tested. The interop layer makes 64-bit struct-layout
> assumptions (`PROPVARIANT` = 24 bytes) and uses hand-written vtable calls; both are asserted at
> startup, so on ARM64 the library fails fast rather than corrupting memory if an assumption does not
> hold. But "fails fast rather than corrupts" is not the same as "verified to work".

If you have ARM64 Windows hardware, running `scripts/test-real-audio.ps1` and contributing a record
here would be a genuinely useful contribution.

---

## Wanted records

The following environments are explicitly **not** verified yet. Contributions are welcome:

| Environment | Why it matters |
| --- | --- |
| Windows 10 2004 (build 19041) | Declared minimum supported version, never exercised |
| Windows 10 22H2 (build 19045) | Still widely deployed |
| Windows 11 21H2 (build 22000) | Per-app routing uses a different interface IID below/above this build |
| Windows 11 22H2 (build 22621) | |
| Windows 11 23H2 (build 22631) | |
| **Windows 11 ARM64 (any build)** | Highest-value missing record: build is supported, runtime is untested |
| Any build with no audio endpoints | Confirms the "no devices" paths degrade gracefully |

---

## Contributing

1. Run the real-hardware gate on the machine:

   ```powershell
   pwsh ./scripts/test-real-audio.ps1
   ```

   The script prints the OS build, architecture, device counts, per-app routing availability, and
   the pass/fail/environment-bypass counts for both the integration and mutation suites, then
   verifies that the default device and routing state were restored.

2. Copy the *Record* template below into this file, fill it in, and open a pull request.

3. **Do not include personal data.** Do not paste full device interface paths, application paths,
   user names, machine names, or serial numbers into this file or any public artifact. The script is
   designed not to print them; keep it that way.

### Record template

```markdown
## Record N — <OS edition and release>

| Field | Value |
| --- | --- |
| OS | |
| Build | |
| Architecture | |
| .NET SDK | |
| Library version | |
| Verified by | |
| Verified date | |

### Capability matrix

| Capability | Status | Notes |
| --- | --- | --- |
| Device enumeration (playback) | | |
| Device enumeration (recording) | | |
| Default device read (six flow/role combinations) | | |
| System default device write | | |
| Device volume / mute | | |
| Session enumeration and mapping | | |
| Application volume / mute | | |
| Per-app output routing | | |
| Per-app input routing | | |
| Device events | | |
| Session events | | |
| x64 build | | |
| ARM64 build (if applicable) | | |
```
