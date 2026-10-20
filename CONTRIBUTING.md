# Contributing to HAZEM Tunnel

Thanks for your interest in improving HAZEM Tunnel! This document covers everything you need to get productive quickly.

---

## 🧭 Ground rules

1. **Honesty is a feature.** Never fake statistics, health checks, or connectivity states. "Connected" may only appear after a real through-tunnel health check passes.
2. **Security is non-negotiable.** TLS certificate validation is never disabled — not even behind a flag. Secrets never appear in profiles, logs, or tests in plaintext.
3. **Zero-warnings build policy.** `TreatWarningsAsErrors` is on. Code must build with **0 warnings**.
4. **Tests must stay green.** `scripts\test.ps1` (109+ tests across 4 projects) must pass before any PR is merged.

---

## 🛠 Development setup

### Prerequisites
- Windows 10/11 (x64) — the WPF UI only runs here
- .NET SDK **10** (pinned via [`global.json`](global.json))
- Git
- PowerShell 5.1+ (for the helper scripts)

### Get started

```powershell
git clone https://github.com/hazmmhmdalshadhly12-ui/Tls.git
cd Tls
scripts\build.ps1     # must complete with 0 warnings
scripts\test.ps1      # all tests green
```

> **Linux/macOS?** The full solution builds with plain `dotnet build` (`EnableWindowsTargeting` is already set), but the WPF app itself only *runs* on Windows.

### Project layout

```
src/HAZEM.Tunnel.App/        WPF desktop client (MVVM)
src/HAZEM.Tunnel.Core/       Transports, SOCKS5, profiles, logging
tests/                       xUnit suites (Core, App, integration/loopback)
docs/                        Config schema, manual QA, build report
samples/profiles/            Example profiles (TLS / SSH / SSH-over-TLS / WebSocket)
scripts/                     build.ps1 / test.ps1 / publish.ps1
installer/                   Optional Inno Setup script
```

---

## 🐛 Reporting bugs

Open an issue using the [**bug report template**](.github/ISSUE_TEMPLATE/bug_report.md). Include:

- App version (Dashboard → About)
- Windows version (Win+R → `winver`)
- The **redacted** log file from `%LocalAppData%\HAZEMTunnel\logs\` — **remove passwords, hostnames, and keys first**

## 💡 Suggesting features

Open an issue with the [feature request template](.github/ISSUE_TEMPLATE/feature_request.md). Describe the *problem* you're solving, not just the solution — it helps evaluate alternatives.

---

## 🔀 Pull requests

1. Fork → create a topic branch (`feat/socks-udp`, `fix/log-rotation`, …).
2. Keep PRs focused — one feature or fix per PR.
3. Match existing code style (C# conventions, MVVM boundaries: ViewModels never touch sockets directly).
4. Add/adjust tests for behavior changes.
5. Run `scripts\build.ps1` and `scripts\test.ps1` — both must pass.
6. Write a clear PR description: what changed, why, and how it was tested.

### Commit style

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
fix(logging): guard reopen race during rotation
feat(socks5): add optional UDP-ASSOCIATE stub
docs(readme): clarify SNI field behavior
```

---

## 🚫 Will not be accepted

- Anything that disables TLS validation or weakens host-key verification.
- Features that fabricate connectivity/ping/traffic data.
- Hard-coded credentials, telemetry, or third-party trackers.
- Code that logs secrets (logs are redacted before disk — keep it that way).

---

## 📄 Licensing

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) that covers this project.
