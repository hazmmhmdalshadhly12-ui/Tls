# HAZEM Tunnel

A Windows desktop client that routes **your configured applications** through an encrypted
tunnel — SSH, TLS (with explicit SNI), or WebSocket — and exposes a local **SOCKS5 proxy**
on `127.0.0.1`.

> **v1.1.0** fixes the v1.0.0 startup error dialog
> *"The process cannot access the file …\logs\hazem-*.log because it is being used by
> another process"*. See [What changed in v1.1.0](#what-changed-in-v110).

---

## What it is / is not (read first)

| ✅ What it DOES | ❌ What it does NOT do |
|---|---|
| Expose a local SOCKS5 proxy (default `127.0.0.1:1080`) | Route **all** system traffic (no system-wide VPN — planned) |
| Show **Connected** only after a real through-tunnel health check | Fabricate ping/latency/traffic numbers |
| Count **real** relayed bytes and rates | Provide "free internet" by itself — a public hostname/SNI never guarantees that |
| Store secrets in the OS credential store (DPAPI), never in profiles | Disable TLS certificate validation (impossible by design) |
| Verify SSH host keys (TOFU / Strict / Pinned) | Log passwords, keys, or traffic contents |

Only apps that are *configured to use the proxy* are tunneled. Everything else connects
directly, unchanged.

---

## Download & run

1. Get `HAZEM-Tunnel-1.1.0-win-x64.zip` (from `dist/` or Releases).
2. Extract → run `HAZEM.Tunnel.exe`. **No .NET install needed** (self-contained, x64).
3. Windows requirements: Windows 10 1809+ / Windows 11, x64.

### SmartScreen note (unsigned build)
This EXE is **not code-signed**. Windows SmartScreen may show *"Windows protected your PC"*.
Click **More info → Run anyway**, or right-click the EXE → Properties → Unblock.
This is expected for self-published builds; future releases may be signed.

---

## What changed in v1.1.0

The v1.0.0 error dialog appeared when the log file was momentarily held by another process
(second app instance, antivirus scan, log viewer) and the file logger threw during
rotation/reopen. v1.1.0 fixes it at three levels:

1. **The file logger can never throw.** All file I/O is guarded; on failure it degrades
   silently (sidecar file → drop) instead of surfacing an exception into the app.
2. **One persistent log stream**, opened with `FileShare.Read`, rotation done under the
   same lock as writes — no reopen races.
3. **Single-instance mutex.** A second launch now shows a friendly message instead of
   fighting over profiles/credentials/log files. (This was the most common trigger.)
4. Global exception handlers **ignore logging failures** — they can no longer pop the
   error dialog even if something else in the logging pipeline changes.

---

## Build from source

Requires .NET SDK 10 (see `global.json`):

```powershell
scripts\build.ps1     # build (0 warnings policy)
scripts\test.ps1      # run all unit tests (3 projects, live loopback suites)
scripts\publish.ps1   # self-contained win-x64 single-file EXE + zip
```

The full solution builds on Linux too (`dotnet build` with `EnableWindowsTargeting`
already set in the App project); only *running* the WPF UI requires Windows.

---

## Usage in 60 seconds

1. **Profiles → ＋ New** → pick transport → fill fields → **Validate → Save**.
   - SSH: host/port/username + password or PEM key (stored via DPAPI, not in the file).
   - TLS: dial host/port + **SNI** (kept separate from dial host on purpose).
   - WebSocket: full `wss://host:port/path` URL.
   - Remote forward target (SSH transports): what the SSH server connects to (the exit).
2. **Dashboard** → select profile → **Connect**.
   Stages run: proxy → transport → **through-tunnel health check** → Connected.
3. Point any app at `127.0.0.1:1080` (SOCKS5). Examples:
   - `curl --proxy socks5h://127.0.0.1:1080 https://ifconfig.me`
   - Browsers: use a proxy extension (e.g. FoxyProxy) → SOCKS5 → 127.0.0.1 → 1080.
4. **Statistics / Logs** pages show real counters and redacted logs.

## Where my data lives

| Data | Location |
|---|---|
| Profiles | `%LocalAppData%\HAZEMTunnel\profiles\*.json` |
| Secrets (DPAPI blobs) | `%LocalAppData%\HAZEMTunnel\data\credentials\` |
| Known SSH hosts | `%LocalAppData%\HAZEMTunnel\data\known-hosts.json` |
| Rotating logs | `%LocalAppData%\HAZEMTunnel\logs\hazem-YYYYMMDD.log` (size-sequence rotation, retention-cleaned) |

## Documentation

- [`docs/CONFIG_SCHEMA.md`](docs/CONFIG_SCHEMA.md) — strict v1 profile schema + import/export rules
- [`docs/MANUAL_TESTING.md`](docs/MANUAL_TESTING.md) — manual QA checklist
- [`docs/BUILD_REPORT.md`](docs/BUILD_REPORT.md) — exact build/test/publish steps and honest scope
- [`installer/`](installer/) — optional Inno Setup installer script

## Security model

- TLS validation is **always on** (system roots + optional pinned CA PEMs that *add* trust).
- SSH host keys: first-use pinned locally; a changed key always fails the session (TOFU/Strict/Pinned).
- Secrets live in DPAPI-encrypted blobs; profiles carry only opaque `{ "key": … }` references.
- Logs are redacted (passwords/tokens/PEM bodies/pattern-registered secrets) before hitting disk.
- The SOCKS5 proxy binds to loopback unless you explicitly enable LAN binding (with a warning).

## Known limitations (honest)

- No system-wide VPN (Wintun-based) — marked "Planned" in the UI.
- The EXE is unsigned (SmartScreen, see above).
- WPF app runs on Windows only; the tooling builds on any OS.
- SOCKS5 supports CONNECT only (no BIND/UDP-ASSOCIATE) — sufficient for HTTP(S) and most TCP clients.

## License

MIT. Third-party: SSH.NET (MIT), .NET runtime (MIT).
