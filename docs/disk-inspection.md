# Inspect sectors, blocks, and file offsets

`a2 disk inspect` displays the bytes of a raw or sector-data 2IMG disk image.
Choose one address per invocation. All command-line address values are decimal.

```sh
a2 disk inspect demo.do --track 18 --sector 14
a2 disk inspect demo.po --block 144
a2 disk inspect demo.2mg --offset 73800 --json
```

On a 140 KiB floppy, track and sector use DOS logical numbering (tracks 0–34,
sectors 0–15). Block numbers use ProDOS logical numbering (blocks 0–279).
Each 512-byte block is displayed as two 256-byte sector ranges in block order.
Their host offsets may be noncontiguous in DOS-order images. Larger supported
images use 512-byte blocks.

The offset selects the sector or block containing that host-file byte. Offsets
include the 2IMG header and any padding before the payload. Choose an offset
inside the payload. The inspector reports hex and printable ASCII, absolute
file offsets, payload-relative offsets, and each range's position within its
ProDOS block.

The inspector recognizes sector order from supported filesystems and 2IMG
headers. For a raw 140 KiB image whose filesystem is unrecognized, specify
`--input-order dos` or `--input-order prodos`. The command opens the image for
reading. In JSON output, `hex` contains two uppercase hexadecimal digits per byte
and `ascii` substitutes a period for each byte outside printable ASCII.
