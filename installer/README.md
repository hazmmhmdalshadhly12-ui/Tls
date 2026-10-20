# Installer (optional)

`hazem-tunnel.iss` is an Inno Setup 6 script.

## Build it (Windows only)

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php).
2. Produce the EXE first:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```
3. Compile:
   ```
   ISCC.exe installer\hazem-tunnel.iss
   ```
4. Output: `dist\HAZEM-Tunnel-1.1.0-setup.exe`

## Notes

- The installer installs per-user (no admin required) when built with the included
  `PrivilegesRequired=lowest`.
- The build is unsigned; the installer carries the same SmartScreen caveat as the EXE.
- Most users can simply use the portable zip — no installation needed.
