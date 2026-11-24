"""Verify the OpenAL runtime matrix and native CPU architectures without executing foreign binaries."""
import argparse
import json
from pathlib import Path
import re
import struct

ROOT = Path(__file__).resolve().parents[1]
RIDS = json.loads((ROOT / "Packaging/NativePlatforms.json").read_text())["retained"]


def pe_imports(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    sections, optional_size = struct.unpack_from("<H12xH", data, pe + 6)
    optional = pe + 24
    directories = optional + (112 if struct.unpack_from("<H", data, optional)[0] == 0x20B else 96)
    import_rva = struct.unpack_from("<I", data, directories + 8)[0]
    section_table = optional + optional_size

    def offset(rva):
        for index in range(sections):
            entry = section_table + index * 40
            virtual_size, virtual, raw_size, raw = struct.unpack_from("<IIII", data, entry + 8)
            if virtual <= rva < virtual + max(virtual_size, raw_size):
                return raw + rva - virtual
        raise ValueError(f"Unmapped RVA {rva:x}")

    cursor = offset(import_rva)
    names = []
    while any(data[cursor:cursor + 20]):
        name_rva = struct.unpack_from("<I", data, cursor + 12)[0]
        start = offset(name_rva)
        names.append(data[start:data.index(0, start)].decode("ascii"))
        cursor += 20
    return names


def verify(root, rids):
    for rid in rids:
        name = "soft_oal.dll" if rid.startswith("win-") else "libopenal.dylib" if rid.startswith("osx-") else "soft_oal.framework/soft_oal" if rid.startswith("ios-") else "libopenal.so"
        path = root / rid / "native" / name
        if not path.is_file():
            raise ValueError(f"Missing native artifact: {path}")
        data = path.read_bytes()
        if rid.startswith("win-"):
            pe = struct.unpack_from("<I", data, 0x3C)[0]
            machine = struct.unpack_from("<H", data, pe + 4)[0]
            expected = 0xAA64 if rid.endswith("arm64") else 0x8664
            assert machine == expected, f"Wrong PE architecture: {path}"
            imports = pe_imports(data)
            forbidden = re.compile(r"vcruntime|msvcp|msvcr\d|libgcc|libstdc\+\+|libwinpthread|libc\+\+", re.I)
            assert not any(forbidden.search(item) for item in imports), (path, imports)
        elif rid.startswith(("linux-", "android-")):
            assert data[:4] == b"\x7fELF" and data[4] == 2, f"Expected ELF64: {path}"
            expected = 183 if rid.endswith("arm64") else 62
            assert struct.unpack_from("<H", data, 18)[0] == expected, f"Wrong ELF architecture: {path}"
        else:
            assert data[:4] == b"\xcf\xfa\xed\xfe", f"Expected a thin Mach-O 64-bit binary: {path}"
            expected = 0x100000C if rid.endswith("arm64") else 0x1000007
            assert struct.unpack_from("<I", data, 4)[0] == expected, f"Wrong Mach-O architecture: {path}"
        print(f"PASS {rid}: {path.stat().st_size} bytes")
    present = {entry.name for entry in root.iterdir() if entry.is_dir()}
    assert present <= set(RIDS), f"Unsupported runtime folders: {present - set(RIDS)}"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("--rid", choices=RIDS, action="append")
    args = parser.parse_args()
    verify(args.root, args.rid or RIDS)
