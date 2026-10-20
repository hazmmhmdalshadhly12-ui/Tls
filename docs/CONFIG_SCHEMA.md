# HAZEM Tunnel — Profile Configuration Schema (v1)

Profiles are JSON files stored in `%LocalAppData%\HAZEMTunnel\profiles\profile-<id>.json`.
The parser is **strict**: unknown fields are rejected (typo protection), raw secret values
are rejected where references belong, and `schemaVersion` must be `1` (older drafts migrate).

## Top level

| Field | Type | Notes |
|---|---|---|
| `schemaVersion` | int | Must be `1` (0 accepted and migrated) |
| `id` | string | Stable GUID ("N" format); generated if missing |
| `name` | string | Required, non-empty |
| `transportKind` | enum | `TlsDirect` \| `Ssh` \| `TlsSsh` \| `WebSocket` |
| `tls` | object | Required for `TlsDirect`/`TlsSsh` |
| `ssh` | object | Required for `Ssh`/`TlsSsh` |
| `webSocket` | object | Required for `WebSocket` |
| `localProxy` | object | Always applied |
| `retry` | object | Bounded retry policy |
| `connectTimeoutMs` | int | Overall connect budget (default 30000) |
| `autoReconnect` | bool | Default true |

## `tls`

| Field | Type | Notes |
|---|---|---|
| `host` | string | DNS/IP dial target |
| `port` | int | 1–65535 |
| `sni` | string | ClientHello ServerName; **separate concept** from host; empty = use host |
| `validateCertificates` | bool | Always true; `false` is rejected at validation |
| `trustedRootPem` | string? | Extra CA(s) in PEM — *adds* trust, never replaces system roots |
| `handshakeTimeoutMs` | int | ≥ 3000 |

## `ssh`

| Field | Type | Notes |
|---|---|---|
| `host`, `port` | string, int | SSH server |
| `username` | string | Required |
| `authMode` | enum | `Password` \| `PrivateKey` |
| `passwordRef` | ref | `{ "key": "..." }` — credential-store reference (raw strings rejected) |
| `privateKeyRef` | ref | PEM content reference |
| `passphraseRef` | ref? | Optional key passphrase |
| `hostKeyPolicy` | enum | `TrustOnFirstUse` \| `Strict` \| `Pinned` |
| `pinnedHostKeyFingerprint` | string? | Required for `Pinned`: `SHA256:` + 43 base64 chars |
| `connectTimeoutMs` | int | ≥ 3000 |
| `remoteForwardHost`, `remoteForwardPort` | string, int | The exit destination behind the SSH server |

## `webSocket`

| Field | Type | Notes |
|---|---|---|
| `url` | string | `ws://` or `wss://host:port/path` (path required) |
| `headers` | map<string,string> | Extra handshake headers; `Host` and `Sec-WebSocket-*` rejected |
| `subprotocol` | string? | Optional |
| `handshakeTimeoutMs` | int | ≥ 3000 |

## `localProxy`

| Field | Type | Notes |
|---|---|---|
| `bindAddress` | string | Literal IP; non-loopback requires `allowLan: true` |
| `port` | int | 1–65535 |
| `allowLan` | bool | Security gate for non-loopback binds |
| `authMode` | enum | `None` \| `UsernamePassword` |
| `usernameRef`, `passwordRef` | ref | Required when `UsernamePassword` |
| `idleTimeoutMs` | int | ≥ 10000 |
| `maxClients` | int | 1–1024 (bounded concurrency) |

## `retry`

| Field | Type | Notes |
|---|---|---|
| `maxAttempts` | int | 1–100 |
| `baseDelayMs` | int | 100–60000 |
| `maxDelayMs` | int | ≥ baseDelayMs |
| `jitterRatio` | double | 0–1 (± fraction of the delay) |

## Import / export

- **Export** writes the profile with reference keys only — secrets never leave the DPAPI store.
- **Import** runs parse → validate → *preview dialog* (Add vs Replace) → commit only on approval.
- A profile with missing credentials still imports; the UI prompts you to re-enter them.

## Concepts kept separate (common mistake)

`TLS host` (dial) ≠ `TLS SNI` (ClientHello name) ≠ `SSH host` (auth target) ≠
`remoteForwardHost` (exit destination) ≠ `WebSocket URL`. The validator treats them as
independent fields; filling one never implies the others.
