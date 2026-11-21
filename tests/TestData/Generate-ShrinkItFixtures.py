# SPDX-FileCopyrightText: 2026 Amos Anderson
# SPDX-License-Identifier: GPL-2.0-only
"""Generate independent native NuFX archives from original test data."""

from binascii import crc_hqx
from itertools import groupby
from pathlib import Path
from struct import pack, pack_into

ROOT = Path(__file__).resolve().parent


def lzw9(data):
    # A fresh dictionary and nine-bit little-endian codes suffice for our small v1 record.
    dictionary = {bytes([i]): i for i in range(256)}
    codes = []
    word = b""
    for byte in data:
        candidate = word + bytes([byte])
        if candidate in dictionary:
            word = candidate
        else:
            codes.append(dictionary[word])
            dictionary[candidate] = len(dictionary) + 1  # 256 is the clear code.
            word = bytes([byte])
    codes.append(dictionary[word])
    assert len(dictionary) < 510
    bits = sum(code << (9 * i) for i, code in enumerate(codes))
    return bits.to_bytes((len(codes) * 9 + 7) // 8, "little")


def shrinkit_rle(data, method):
    # Original ShrinkIt stores a CRC over fully padded 4 KiB blocks.
    padded = data.ljust((len(data) + 4095) // 4096 * 4096, b"\0")
    result = bytearray(pack("<H", crc_hqx(padded, 0))) if method == 2 else bytearray()
    result.extend((42, 0xDB))
    for offset in range(0, len(data), 4096):
        block = data[offset:offset + 4096].ljust(4096, b"\0")
        encoded = bytearray()
        for value, run in groupby(block):
            remaining = sum(1 for _ in run)
            while remaining:
                count = min(remaining, 256)
                if count >= 4 or value == 0xDB:
                    encoded.extend((0xDB, value, count - 1))
                else:
                    encoded.extend([value] * count)
                remaining -= count
        stored = encoded if len(encoded) < 4096 else block
        result.extend(pack("<H", len(stored)))
        if method == 2:
            result.append(1)
            result.extend(lzw9(stored))
        else:
            result.extend(stored)
    return bytes(result)


def record(name, threads, file_type=0, aux=0, storage=1, version=3):
    name = name.encode("ascii")
    header = bytearray(60)
    header[:4] = bytes.fromhex("4ef546d8")
    pack_into("<HHIHHIIIH", header, 6, 60, version, len(threads), 1, ord(":"),
              0xC3, file_type, aux, storage)
    pack_into("<H", header, 58, len(name))
    header.extend(name)
    body = bytearray()
    for kind, method, plain, stored in threads:
        header.extend(pack("<HHHHII", 2, method, kind, crc_hqx(plain, 0xFFFF) if version == 3 else 0,
                           0 if kind == 1 else len(plain), len(stored)))
        body.extend(stored)
    pack_into("<H", header, 4, crc_hqx(header[6:], 0))
    return header + body


def archive(records):
    body = b"".join(records)
    header = bytearray(48)
    header[:6] = bytes.fromhex("4ef546e96ce5")
    pack_into("<I", header, 8, len(records))
    pack_into("<H", header, 28, 2)
    pack_into("<I", header, 38, len(header) + len(body))
    pack_into("<H", header, 6, crc_hqx(header[8:], 0))
    return header + body


data = b"Hello, ShrinkIt!\r"
resource = bytes.fromhex("007f80ff")
legacy = b"6502" * 20
disk = (ROOT / "independent-dos33.po").read_bytes()
(ROOT / "independent-files.shk").write_bytes(archive([
    record("DOCS:HELLO", [(0, 0, data, data), (2, 0, resource, resource)], 4, 0x1234, 5),
    record("EMPTY", [(0, 0, b"", b"")]),
    record("EMPTY", [(0, 0, b"2", b"2")]),
    record("LEGACY", [(0, 2, legacy, shrinkit_rle(legacy, 2))], version=1),
    record("ONLY.RSRC", [(2, 0, resource, resource)], storage=5),
    record("ONLY.DATA", [(0, 0, data, data)], storage=5),
]))
(ROOT / "independent-disk.sdk").write_bytes(archive([
    record("DOS.DISK", [(1, 3, disk, shrinkit_rle(disk, 3))], aux=len(disk) // 512, storage=512),
]))
