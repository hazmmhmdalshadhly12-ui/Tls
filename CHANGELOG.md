# Changelog

All notable changes to **HAZEM Tunnel** are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.1.0] — 2026-10

### Fixed — startup log error dialog
v1.0.0 could show an error dialog at startup when the log file was momentarily held by
another process (second app instance, antivirus scan, log viewer):

> *The process cannot access the file `…\logs\hazem-*.log` because it is being used by another process.*

The file logger threw during rotation/reopen and the exception surfaced into the UI.
Fixed at four levels:

- **The file logger can never throw.** All file I/O is guarded; on failure it degrades
  silently (sidecar file → drop) instead of surfacing an exception into the app.
- **One persistent log stream**, opened with `FileShare.Read`; rotation happens under the
  same lock as writes — no reopen races.
- **Single-instance mutex.** A second launch now shows a friendly message instead of
  fighting over profiles/credentials/log files (the most common trigger).
- Global exception handlers **ignore logging failures** — they can no longer pop the
  error dialog even if something else in the logging pipeline changes.

### Added
- `.gitignore`, CI workflow, issue templates, community files (LICENSE / SECURITY / CONTRIBUTING / CHANGELOG).

---

## [1.0.0] — 2026-10

### Added — first public release

**Transports**
- TLS with explicit SNI control (dial endpoint and SNI hostname kept separate on purpose).
- SSH (SSH.NET): password or PEM private key; host-key policies TOFU / Strict / Pinned.
- SSH-over-TLS bridge — two encrypted layers in one connection.
- WebSocket (`ws://` / `wss://`) with configurable path and headers.

**Local proxy**
- SOCKS5 server (RFC 1928) with optional username/password auth (RFC 1929), CONNECT only.
- Binds to `127.0.0.1` by default; LAN binding is opt-in with an explicit warning.

**Desktop app**
- WPF (MVVM, dependency injection), .NET 10, self-contained win-x64 single-file publish.
- Dashboard with staged connect flow: proxy → transport → through-tunnel health check → Connected.
- Real byte counters and rates — no fabricated statistics anywhere.
- Versioned JSON profile schema (v1) with import/export; sample profiles included.
- Secrets stored via Windows DPAPI; profiles reference them by opaque key only.
- Redacted rotating file logs with retention cleanup.
- In-app update-safe single instance; health check gates the "Connected" state.

**Quality**
- xUnit test suites across 3 projects (84 tests) including live loopback suites.
- Zero-warning build policy (`TreatWarningsAsErrors`).
- Scripts: `scripts/build.ps1`, `scripts/test.ps1`, `scripts/publish.ps1`.
- Docs: config schema, manual QA checklist, honest build report.

[1.1.0]: https://github.com/hazmmhmdalshadhly12-ui/Tls/releases/tag/v1.1.0
[1.0.0]: https://github.com/hazmmhmdalshadhly12-ui/Tls/releases/tag/v1.0.0
