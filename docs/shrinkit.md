# ShrinkIt archives

Use `a2 archive` to read native NuFX file archives (`.shk`) and disk archives (`.sdk`).
The command recognizes the NuFX signature regardless of the host filename.

```sh
a2 archive ls tools.shk
a2 archive extract tools.shk 'LIB:PRINT' --part data --to print.bin
a2 archive extract tools.shk --index 3 --part resource --to print.rsrc
a2 archive catalog system.sdk --recursive
a2 archive extract system.sdk --to system.po
```

`ls` shows one-based record indices, original stored names, available parts,
decoded byte counts, and compression methods. Add `--json` for stored byte
counts, ProDOS type, auxiliary type, access flags, diagnostics, and the archive
SHA-256. The same name can appear more than once. Use `--index` to select a
specific record, or supply the exact stored name including any path separator.

`extract` selects the data fork of a file or the disk image of a disk record by
default. Use `--part resource` for a resource fork. You choose the output file,
including its directory, with `--to`. An existing file requires `--overwrite`.
Extraction checks the decoded length and available checksums and validates the
staged output before replacing its destination. Each fork is a separate byte
stream. A raw disk image retains its original block-order bytes.

`catalog` selects the only disk record automatically. With multiple disk records,
choose a name or index. It reads the disk in ProDOS block order and accepts the
usual filesystem and order overrides. Add `--json` for the archive path, selected
record, decoded disk SHA-256 and catalog. For other disk operations, extract the
image and use the `disk` commands.

The reader accepts up to 16,384 records and 64 MiB each for the archive and
extracted part. Cataloging uses the disk reader's 34 MiB image limit. Supported
methods include stored, Squeeze, ShrinkIt LZW/1, GS/ShrinkIt LZW/2, and Unix
compress (12- and 16-bit). Binary II wrappers, self-extracting archives,
archive creation, and archive updates are outside this interface.
