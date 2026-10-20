# HAZEM Tunnel — Manual Testing Checklist

Run on a Windows 10/11 x64 machine. Every item lists the **expected** result.
Automated coverage: 101 xUnit tests (`scripts\test.ps1`).

## 1. Startup & single instance (the v1.1.0 fix)

| # | Step | Expected |
|---|---|---|
| 1.1 | Run `HAZEM.Tunnel.exe` | Window opens, dark navy theme, 8 sidebar pages |
| 1.2 | Run the EXE **again** while the first is open | Info dialog "HAZEM Tunnel is already running" — second process exits cleanly |
| 1.3 | Start the app while an antivirus is actively scanning `logs\` | No error dialog; app starts; logs keep flowing (possibly to `hazem-*-locked.log` sidecar) |
| 1.4 | Open the log file in Notepad while the app writes | No app errors; Notepad reads fine (`FileShare.Read`) |

## 2. Profiles

| # | Step | Expected |
|---|---|---|
| 2.1 | Profiles → ＋ New → SSH → fill host/user → Set password → Validate | Green "Profile is valid" |
| 2.2 | Save → reopen editor | Password field shows "stored ✔" (not the value) |
| 2.3 | Set SNI to a URL like `https://x` | Validation fails: "not a valid SNI hostname" |
| 2.4 | Try to set `validateCertificates=false` via import | Import blocked with explicit reason |
| 2.5 | Export profile → open JSON | No password/key material; only `{"key": ...}` references |
| 2.6 | Import that JSON on another machine | Imports; prompts to re-enter secrets |

## 3. Connectivity (SSH transport)

| # | Step | Expected |
|---|---|---|
| 3.1 | Connect to a real SSH server (password) | Stages progress; Connected **only after** "Health check passed" |
| 3.2 | Wrong password | Fails with clear auth message; bounded retries then "Giving up after N attempts" |
| 3.3 | Server with changed host key (after first use) | Session rejected: host key mismatch (fail-closed) |
| 3.4 | Pinned policy with wrong fingerprint | Rejected before auth |
| 3.5 | `curl --proxy socks5h://127.0.0.1:1080 https://ifconfig.me` | Works; exit IP = remote forward path |

## 4. Health check honesty

| # | Step | Expected |
|---|---|---|
| 4.1 | Connect with working tunnel | "CONNECTED … Health check passed in X ms" |
| 4.2 | Block the exit (e.g. firewall the forward target) but keep SSH alive | Health fails → after 3 consecutive failures the tunnel restarts automatically |
| 4.3 | Kill network during Connected | Reconnecting… → recovers or fails honestly (never fake Connected) |

## 5. Statistics

| # | Step | Expected |
|---|---|---|
| 5.1 | Download a large file through the tunnel | Bytes Sent/Received grow with real numbers; chart moves |
| 5.2 | Idle tunnel | Rates 0 B/s (no synthetic activity) |
| 5.3 | Open 4 parallel curls | Active pipes = 4 |

## 6. Logs & redaction

| # | Step | Expected |
|---|---|---|
| 6.1 | Trigger a failed auth | Log shows failure; password value absent (masked `*****`) |
| 6.2 | Logs → Open log folder | Opens `%LocalAppData%\HAZEMTunnel\logs` |
| 6.3 | Let a log exceed the size cap | Same-day `-1.log`, `-2.log` sequence appears |
| 6.4 | Backdate a log file beyond retention | Deleted at next startup |

## 7. Shutdown

| # | Step | Expected |
|---|---|---|
| 7.1 | Disconnect while 2 pipes are active | Pipes close; "SOCKS5 proxy stopped"; state Disconnected |
| 7.2 | Close window while connected | App exits; no orphan process; mutex released; log file not locked after exit |

## 8. WebSocket transport

| # | Step | Expected |
|---|---|---|
| 8.1 | wss:// endpoint against a WS echo server | Handshake OK; relay works through SOCKS5 |
| 8.2 | Try to add `Sec-WebSocket-Key` header | Editor validation rejects it |
