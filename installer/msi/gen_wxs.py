#!/usr/bin/env python3
"""
gen_wxs.py — Fill installer/msi/HAZEMTunnel.wxs.template with explicit
Component/File authoring for every file in the self-contained publish folder.

Why explicit components instead of WiX v5 <Files> harvesting?
  - Classic Component/File authoring is fully deterministic, reviewable, and
    works identically across WiX versions — no undocumented harvesting rules.

The output .wxs declares ONE component per published file (KeyPath on the
file, auto GUID) plus a matching ComponentRef inside the Main feature.

Usage (repository root):
  python installer/msi/gen_wxs.py \
      --publish-dir publish/win-x64 \
      --template installer/msi/HAZEMTunnel.wxs.template \
      --out obj/msi/HAZEMTunnel.wxs

Exits non-zero on any problem so CI fails fast instead of shipping a broken MSI.
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path
from xml.sax.saxutils import escape, quoteattr

# Files that must never ship inside the installer payload.
EXCLUDE_SUFFIXES = (".pdb", ".xml.temp", ".exe.config-temp")


def sanitize_component_id(rel_path: str) -> tuple[str, str]:
    """Deterministic short (<= 60 chars) MSI-safe identifier for a file path."""
    import hashlib
    digest = hashlib.sha256(rel_path.encode("utf-8")).hexdigest()[:10].upper()
    safe = "".join(ch if (ch.isalnum()) else "_" for ch in rel_path)[:38].strip("_")
    return f"cmp_{safe}_{digest}", f"fil_{safe}_{digest}"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--publish-dir", required=True)
    ap.add_argument("--template", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    publish = Path(args.publish_dir)
    template = Path(args.template)
    out = Path(args.out)
    if not publish.is_dir():
        print(f"ERROR: publish dir not found: {publish}", file=sys.stderr)
        return 2
    if not template.is_file():
        print(f"ERROR: template not found: {template}", file=sys.stderr)
        return 2

    files = sorted(
        p for p in publish.rglob("*") if p.is_file()
        and not p.name.lower().endswith(EXCLUDE_SUFFIXES)
    )
    if not files:
        print("ERROR: publish dir contains no files — refusing to generate an empty MSI",
              file=sys.stderr)
        return 3
    if not any(p.name == "HAZEM.Tunnel.exe" for p in files):
        print("ERROR: HAZEM.Tunnel.exe missing from publish dir — wrong payload?",
              file=sys.stderr)
        return 3
    # The app must run from the installed folder: hostfxr must be there (self-contained).
    if not any(p.name == "hostfxr.dll" for p in files):
        print("ERROR: hostfxr.dll missing — publish was not self-contained",
              file=sys.stderr)
        return 3

    components: list[str] = []
    refs: list[str] = []
    for p in files:
        rel = p.relative_to(publish).as_posix()
        comp_id, file_id = sanitize_component_id(rel)
        name_attr = quoteattr(p.name)
        src_attr = quoteattr(f"$(PublishDir){rel}")
        components.append(
            f'        <Component Id="{comp_id}" Guid="*">\n'
            f'          <File Id="{file_id}" Name={name_attr} Source={src_attr} '
            f'KeyPath="yes" Checksum="yes" />\n'
            f'        </Component>'
        )
        refs.append(f'      <ComponentRef Id="{comp_id}" />')

    block_components = "\n".join(components)
    block_refs = "\n".join(refs)

    tpl = template.read_text(encoding="utf-8")
    if "@@HARVESTED_COMPONENTS@@" not in tpl or "@@HARVESTED_COMPONENTREFS@@" not in tpl:
        print("ERROR: template missing @@HARVESTED_*@@ placeholders", file=sys.stderr)
        return 4
    result = tpl.replace("@@HARVESTED_COMPONENTS@@", block_components) \
                .replace("@@HARVESTED_COMPONENTREFS@@", block_refs)

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(result, encoding="utf-8")

    total_bytes = sum(p.stat().st_size for p in files)
    print(f"gen_wxs: {len(files)} files ({total_bytes:,} bytes) -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
