#!/usr/bin/env python3
"""Verify a packaged Windows demo without loading or launching its assembly.

Only the JSON evidence is written; bundled files are inspected in memory.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import zlib


BUNDLE_SIGNATURE = bytes.fromhex(
    "8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae"
)
MAX_ASSEMBLY_BYTES = 256 * 1024 * 1024


class VerificationError(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise VerificationError(message)


class BundleReader:
    def __init__(self, data: bytes, offset: int):
        self.stream = io.BytesIO(data)
        self.stream.seek(offset)

    def read(self, length: int) -> bytes:
        value = self.stream.read(length)
        require(len(value) == length, "The single-file bundle header is truncated.")
        return value

    def number(self, format_string: str) -> int:
        return struct.unpack(format_string, self.read(struct.calcsize(format_string)))[0]

    def string(self) -> str:
        length = 0
        for shift in range(0, 35, 7):
            current = self.number("<B")
            length |= (current & 127) << shift
            if current < 128:
                require(length <= 4096, "A bundle header string exceeds the reviewed limit.")
                return self.read(length).decode("utf-8")
        raise VerificationError("The bundle contains an invalid length-prefixed string.")


def pe_subsystem(data: bytes) -> int:
    require(len(data) >= 64 and data[:2] == b"MZ", "The app is not a Windows PE executable.")
    pe_offset = struct.unpack_from("<I", data, 0x3C)[0]
    require(0 <= pe_offset <= len(data) - 24, "The Windows PE header offset is invalid.")
    require(data[pe_offset : pe_offset + 4] == b"PE\0\0", "The Windows PE signature is invalid.")
    optional_size = struct.unpack_from("<H", data, pe_offset + 20)[0]
    optional_offset = pe_offset + 24
    require(optional_size >= 70 and optional_offset + optional_size <= len(data),
            "The Windows PE optional header is invalid.")
    require(struct.unpack_from("<H", data, optional_offset)[0] in (0x10B, 0x20B),
            "The Windows PE optional header format is unsupported.")
    subsystem = struct.unpack_from("<H", data, optional_offset + 68)[0]
    require(subsystem == 2, "The packaged app must use Windows GUI subsystem 2.")
    return subsystem


def bundled_assembly(data: bytes, assembly_name: str) -> tuple[bytes, dict[str, int]]:
    signature_offset = data.find(BUNDLE_SIGNATURE)
    require(signature_offset >= 8, "The apphost single-file bundle signature was not found.")
    header_offset = struct.unpack_from("<q", data, signature_offset - 8)[0]
    require(0 <= header_offset < len(data), "The single-file bundle header offset is invalid.")
    reader = BundleReader(data, header_offset)
    major = reader.number("<I")
    minor = reader.number("<I")
    require(major in (1, 2, 6), f"Unsupported single-file bundle format: {major}.{minor}.")
    count = reader.number("<i")
    require(0 < count <= 10000, "The bundle file count is invalid.")
    reader.string()  # Bundle identity, not a filesystem path.
    if major >= 2:
        reader.read(40)  # deps/runtimeconfig locations and bundle flags.
    matches: list[tuple[int, int, int]] = []
    for _ in range(count):
        offset = reader.number("<q")
        size = reader.number("<q")
        compressed_size = reader.number("<q") if major >= 6 else 0
        kind = reader.number("<B")
        name = reader.string()
        if name == assembly_name:
            require(kind == 1, "The app assembly bundle entry has an unexpected type.")
            require(0 < size <= MAX_ASSEMBLY_BYTES and compressed_size >= 0,
                    "The app assembly bundle entry has an invalid size.")
            stored_size = compressed_size or size
            require(offset >= 0 and offset <= len(data) - stored_size,
                    "The app assembly bundle payload is outside the executable.")
            matches.append((offset, size, compressed_size))
    require(len(matches) == 1, "The bundle must contain exactly one matching app assembly.")
    offset, size, compressed_size = matches[0]
    payload = data[offset : offset + (compressed_size or size)]
    if compressed_size:
        decompressor = zlib.decompressobj(-zlib.MAX_WBITS)
        assembly = decompressor.decompress(payload, size + 1)
        require(decompressor.eof and not decompressor.unused_data and
                not decompressor.unconsumed_tail,
                "The app assembly's compressed bundle stream is invalid.")
    else:
        assembly = payload
    require(len(assembly) == size, "The app assembly's extracted size does not match its bundle entry.")
    return assembly, {
        "formatMajor": major,
        "formatMinor": minor,
        "fileCount": count,
        "compressedSizeBytes": compressed_size,
    }


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def verify(app_path: Path, assembly_path: Path, ui_path: Path) -> dict:
    app = app_path.read_bytes()
    subsystem = pe_subsystem(app)
    assembly, bundle = bundled_assembly(app, assembly_path.name)
    expected_assembly = assembly_path.read_bytes()
    require(assembly == expected_assembly,
            "The executable's bundled assembly differs from the supplied current build assembly.")
    require(ui_path.is_dir(), "The current UI build directory does not exist.")
    assets = []
    for path in sorted(ui_path.rglob("*")):
        if not path.is_file():
            continue
        relative = path.relative_to(ui_path).as_posix()
        payload = path.read_bytes()
        require(payload in assembly, f"The current UI asset is not embedded: {relative}")
        assets.append({"asset": relative, "sizeBytes": len(payload), "sha256": sha256(payload)})
    require(any(item["asset"] == "index.html" for item in assets),
            "The current UI build has no index.html.")
    require(any(item["asset"].startswith("assets/") and item["asset"].endswith(".js")
                for item in assets), "The current UI build has no bundled JavaScript asset.")
    return {
        "schemaVersion": 1,
        "verifiedUtc": datetime.now(timezone.utc).isoformat(),
        "acceptance": "static-package-only; app not loaded or launched",
        "app": {"name": app_path.name, "sizeBytes": len(app), "sha256": sha256(app),
                "peSubsystem": subsystem},
        "assembly": {"name": assembly_path.name, "sizeBytes": len(assembly),
                     "sha256": sha256(assembly), "matchesCurrentBuild": True, "bundle": bundle},
        "assets": assets,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", required=True, type=Path, help="The final packaged Windows EXE.")
    parser.add_argument("--assembly", required=True, type=Path, help="The matching current build DLL.")
    parser.add_argument("--ui", required=True, type=Path, help="The matching current UI dist directory.")
    parser.add_argument("--output", type=Path, help="JSON evidence; defaults beside the EXE.")
    args = parser.parse_args()
    try:
        evidence = verify(args.app, args.assembly, args.ui)
        output = args.output or args.app.parent / "demo-package-evidence.json"
        protected = [args.app.resolve(), args.assembly.resolve()]
        protected.extend(path.resolve() for path in args.ui.rglob("*") if path.is_file())
        require(output.resolve() not in protected, "The evidence output cannot overwrite an input file.")
        output.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    except (VerificationError, OSError, UnicodeError, zlib.error, struct.error) as error:
        print(f"FAIL demo package verification: {error}", file=sys.stderr)
        return 1
    print(f"PASS static package: GUI EXE, matching bundled assembly, {len(evidence['assets'])} embedded UI assets")
    print(f"SHA-256 {evidence['app']['sha256']}")
    print(f"Evidence: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
