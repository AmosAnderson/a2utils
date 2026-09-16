#!/usr/bin/env python3
"""Construct original ProDOS test bytes directly, without a filesystem engine.

Reference: Apple ProDOS 8 Technical Reference, Appendix B, figures B-3/B-4/B-5/B-7/B-8.
https://prodos8.com/docs/techref/file-organization/
Only this script's new independent-prodos.po fixture is written.
"""

import hashlib
from pathlib import Path
import struct


BLOCKS = 280
image = bytearray(BLOCKS * 512)
used = set(range(7))
for block in range(BLOCKS):
    image[block * 512:(block + 1) * 512] = bytes([0x5A]) * 512
    struct.pack_into("<H", image, block * 512, block)


def u16(offset, value):
    struct.pack_into("<H", image, offset, value)


def allocate(block):
    assert block not in used, f"duplicate allocation: {block}"
    used.add(block)
    image[block * 512:(block + 1) * 512] = bytes(512)


def stamp(offset, year, month, day, hour, minute):
    u16(offset, ((year % 100) << 9) | (month << 5) | day)
    u16(offset + 2, (hour << 8) | minute)


def header(block, name, count, parent=None, entry_number=0):
    offset = block * 512
    image[offset + 4] = (0xF0 if parent is None else 0xE0) | len(name)
    image[offset + 5:offset + 5 + len(name)] = name.encode("ascii")
    if parent is None:
        image[offset + 0x14:offset + 0x1C] = bytes.fromhex("75230000c3270d00")
    stamp(offset + 0x1C, 1993, 7, 16, 14, 35)
    image[offset + 0x22:offset + 0x25] = bytes([0xE3, 0x27, 13])
    u16(offset + 0x25, count)
    u16(offset + 0x27, 6 if parent is None else parent)
    if parent is None:
        u16(offset + 0x29, BLOCKS)
    else:
        image[offset + 0x29:offset + 0x2B] = bytes([entry_number, 0x27])


def entry(block, slot, name, storage, key, blocks_used, eof, owner=2, access=0xE3):
    offset = block * 512 + 4 + slot * 39
    image[offset] = (storage << 4) | len(name)
    image[offset + 1:offset + 1 + len(name)] = name.encode("ascii")
    image[offset + 0x10] = 0x0F if storage == 0xD else 0x06
    u16(offset + 0x11, key)
    u16(offset + 0x13, blocks_used)
    image[offset + 0x15:offset + 0x18] = eof.to_bytes(3, "little")
    stamp(offset + 0x18, 1993, 7, 16, 14, 35)
    image[offset + 0x1E] = access
    u16(offset + 0x1F, 0 if storage == 0xD else 0x2000)
    stamp(offset + 0x21, 1994, 2, 3, 8, 9)
    u16(offset + 0x25, owner)


def index(block, pointers):
    allocate(block)
    for slot, target in pointers.items():
        # Each 512-byte index contains 256 low bytes followed by 256 high bytes.
        # The transcription's 0-127/128-255 prose is a typo; see figure B-7.
        image[block * 512 + slot] = target & 0xFF
        image[block * 512 + 256 + slot] = target >> 8


def data(block, payload):
    allocate(block)
    image[block * 512:block * 512 + len(payload)] = payload


# Reserved boot blocks intentionally contain zeros, not Apple boot code.
image[:7 * 512] = bytes(7 * 512)
for block in range(2, 6):
    u16(block * 512, 0 if block == 2 else block - 1)
    u16(block * 512 + 2, 0 if block == 5 else block + 1)
header(2, "INDEPENDENT", 7)

data(32, bytes.fromhex("007f80ff0d"))
entry(2, 1, "SEED", 1, 32, 1, 5, access=0x21)
data(33, bytes(range(256)) * 2)
entry(2, 2, "EDGE512", 1, 33, 1, 512)
index(34, {0: 35, 1: 190})
data(35, bytes([0xA5]) * 512)
data(190, bytes([0x6B]))
entry(2, 3, "SAP513", 2, 34, 3, 513)
index(36, {0: 37, 3: 200})
data(37, bytes([0x3C]) * 512)
data(200, bytes([0xD7]))
entry(2, 4, "SPARSE", 2, 36, 3, 1537)
index(38, {0: 39, 1: 40})
index(39, {0: 41, 255: 201})
index(40, {0: 42})
data(41, bytes([0x11]) * 512)
data(201, bytes([0xFE]) * 512)
data(42, bytes([0x42]))
entry(2, 5, "TREE", 3, 38, 6, 131073)

allocate(43)
header(43, "FULLDIR", 12, parent=2, entry_number=7)
entry(2, 6, "FULLDIR", 0xD, 43, 1, 512)
for number in range(12):
    data(44 + number, b"")
    entry(43, number + 1, f"F{number:02}", 1, 44 + number, 1, 0, owner=43)

allocate(56)
allocate(180)
u16(56 * 512 + 2, 180)
u16(180 * 512, 56)
header(56, "GROWNDIR", 13, parent=2, entry_number=8)
entry(2, 7, "GROWNDIR", 0xD, 56, 2, 1024)
for number in range(13):
    data(57 + number, bytes([0x80 + number]))
    entry(56 if number < 12 else 180, number + 1 if number < 12 else 0,
          f"G{number:02}", 1, 57 + number, 1, 1, owner=56)

for block in range(BLOCKS):
    if block not in used:
        image[6 * 512 + block // 8] |= 0x80 >> (block % 8)

destination = Path(__file__).with_name("independent-prodos.po")
destination.write_bytes(image)
print(f"{hashlib.sha256(image).hexdigest()}  {destination.name}")
print(f"{BLOCKS - len(used)} free blocks; {len(used)} allocated blocks")
