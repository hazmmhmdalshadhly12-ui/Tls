# HAZEM Tunnel v1.1.0 — Build & Delivery Report

**Build host:** Linux container (Debian-based), .NET SDK 10.0.402
**Target:** `win-x64`, self-contained, single-file WPF EXE
**Honesty rule:** every number below comes from an actually executed command.

## 1. Environment

| Item | Value |
|---|---|
| SDK | .NET 10.0.402 (installed via dotnet-install.sh into the workspace) |
| NuGet | reachable; packages restored from nuget.org |
| SSH.NET | 2026.0.0 (MIT) — API verified by reflection before use |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Extensions.* | 10.0.13 |
| xUnit | 2.9.3 + runner 4.0.1 + Test SDK 18.11.0 |

## 2. Solution layout

```
HAZEM.Tunnel.slnx
├── src/HAZEM.Tunnel.Core           (models, validation, statistics, retry, redaction)
├── src/HAZEM.Tunnel.Networking     (TLS/SNI, SSH, TLS→SSH bridge, WebSocket, SOCKS5, relay, engine)
├── src/HAZEM.Tunnel.Infrastructure (JSON profiles, DPAPI credentials, rotating logs, stores)
├── src/HAZEM.Tunnel.App            (WPF, MVVM, 8 pages, dark theme, single-instance)
├── tests/HAZEM.Tunnel.Core.Tests         (56 tests)
├── tests/HAZEM.Tunnel.Configuration.Tests (12 tests)
└── tests/HAZEM.Tunnel.Networking.Tests    (33 tests — live loopback sockets/TLS)
```

## 3. Executed build

```
dotnet restore HAZEM.Tunnel.slnx        → 7/7 projects restored
dotnet build  HAZEM.Tunnel.slnx -c Release
  → 0 Warning(s), 0 Error(s)
dotnet test   HAZEM.Tunnel.slnx -c Release
  → Core.Tests:          56/56 passed
  → Configuration.Tests: 12/12 passed
  → Networking.Tests:    33/33 passed
  → TOTAL: 101/101, verified stable across 3 consecutive full runs
dotnet publish src/HAZEM.Tunnel.App -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:EnableCompressionInSingleFile=true -o publish/win-x64
  → publish/win-x64/HAZEM.Tunnel.exe  (~69 MB)
```

## 4. What the tests actually exercise (no simulation)

- **SOCKS5 (live):** RFC 1928 method negotiation, RFC 1929 user/pass accept/reject,
  CONNECT success/failure codes, 8 concurrent pipes, graceful listener shutdown,
  non-SOCKS5 client dropped.
- **TLS (live):** in-memory self-signed server on loopback; handshake success via pinned
  custom root; untrusted rejection; hostname-mismatch rejection (SNI ≠ SAN); DNS-failure
  and connection-refused categorization.
- **Host keys:** TOFU accept/store, changed-key rejection, Strict, Pinned, fingerprint format.
- **Engine primitives:** bounded retry with exponential backoff/jitter, cancellation
  semantics, thread-safe statistics (8 threads × 10k ops), schema strictness (unknown
  fields, raw secrets, v0 migration), log redaction patterns.

## 5. The v1.0.0 → v1.1.0 log fix (root cause & change list)

**Reported:** dialog *"An error occurred while writing to logger(s). (The process cannot
access the file '…\hazem-20261020.log' because it is being used by another process.)"*

**Root cause:** the rotating file provider could throw (reopen during rotation while the
file was held by another process — typically a second app instance or AV scan), and that
exception escaped the logging pipeline into the global handler, which showed a modal dialog.

**Changes in v1.1.0:**
1. `RotatingFileLoggerProvider` rewritten: never throws; every I/O path guarded; sidecar
   fallback (`hazem-*-locked.log`) when the primary is locked; bounded reopen attempts.
2. Single persistent `FileStream` opened `FileShare.Read`; date/size rotation under the
   same lock as writes; size rotation uses explicit per-day sequence numbers.
3. `Mutex` single-instance guard (`Local\HAZEM.Tunnel.SingleInstance.v1`).
4. Global handlers treat "writing to logger" / "used by another process" exceptions as
   logging failures — recorded in memory, never surfaced as dialogs.

## 6. Scope & limitations (unchanged honesty)

| Capability | Status |
|---|---|
| App-level tunneling via local SOCKS5 | ✅ implemented |
| TLS / SSH / TLS→SSH / WebSocket transports | ✅ implemented |
| Through-tunnel health check before "Connected" | ✅ implemented |
| System-wide VPN (Wintun) | ⛔ NOT implemented — "Planned" badge in UI |
| WPF UI execution on the Linux build host | ⛔ impossible; must run on Windows |
| End-to-end run against a real authorized server | ⛔ not available from this environment (no credentials/network route) |
| Code signing | ⛔ not done; SmartScreen documented |
| Inno Setup installer | script provided; must be compiled on Windows |

## 7. Packaging

- `dist/HAZEM-Tunnel-1.1.0-win-x64.zip` — portable single-file EXE
- `dist/HAZEM-Tunnel-1.1.0-source.zip` — full source snapshot
- `installer/hazem-tunnel.iss` — optional Inno Setup script (Windows)
