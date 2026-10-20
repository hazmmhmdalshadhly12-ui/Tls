# Security Policy

## Supported versions

| Version | Supported |
|---------|-----------|
| 1.1.x   | ✅ Yes    |
| 1.0.x   | ❌ No — please upgrade |
| < 1.0   | ❌ No     |

---

## Reporting a vulnerability

**Do not open a public issue for security problems.**

Use GitHub's [private vulnerability reporting](https://github.com/hazmmhmdalshadhly12-ui/Tls/security/advisories/new) (Security tab → Report a vulnerability). You'll be able to describe the issue privately, and we can coordinate a fix and credit you if you wish.

Please include:

1. Affected version (Dashboard → About, or the release tag).
2. Steps / minimal reproduction (a profile JSON with **redacted** secrets is ideal).
3. Impact assessment — what an attacker could and could not do.
4. Any relevant redacted logs (`%LocalAppData%\HAZEMTunnel\logs\`).

You can expect an initial response within **7 days**. Please give a coordinated-disclosure window of 90 days before any public disclosure.

---

## Security model (what the app guarantees)

| Area | Behavior |
|------|----------|
| TLS | Certificate validation is **always on** — system trust roots, plus optional pinned CA PEMs that only *add* trust. There is no code path that disables validation. |
| SSH host keys | First-use pinning (TOFU), Strict, or Pinned modes. A changed host key **always** fails the session — no silent accept. |
| Secrets | Stored as DPAPI-encrypted blobs under `%LocalAppData%\HAZEMTunnel\data\credentials\`. Profile JSON files carry only opaque `{ "key": … }` references. |
| Logging | Passwords, tokens, PEM bodies, and pattern-registered secrets are redacted before any line reaches disk. |
| Network binding | The SOCKS5 listener binds to `127.0.0.1` by default. LAN binding is opt-in and warns explicitly. |
| Single instance | A mutex prevents concurrent instances from racing on profiles, credentials, and log files. |

---

## Known accepted trade-offs

- The distributed EXE is **not code-signed** (no Authenticode certificate yet). SmartScreen may warn — see the README's [SmartScreen note](README.md#smartscreen-note-unsigned-build). Verify downloads against the SHA-256 published with each release.
- SOCKS5 implements CONNECT only (RFC 1928) — no BIND / UDP-ASSOCIATE.
- The app tunes **per-application** traffic via the local proxy; it is not a system-wide VPN (Wintun integration is planned, not present).

---

## Verification of release artifacts

Each release ships `HAZEM-Tunnel-<version>-win-x64.zip`. The SHA-256 checksum is published alongside it. Verify before running:

```powershell
Get-FileHash .\HAZEM-Tunnel-1.2.0-win-x64.zip -Algorithm SHA256
```
