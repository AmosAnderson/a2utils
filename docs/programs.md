# Apple II program tools

`a2 asm` assembles source into machine code and disassembles binaries into
reassemblable listings. `a2 basic` tokenizes and lists Applesoft BASIC for its
interpreter. Both are native C# operations; no external assembler is required.
Disassembly cannot recover original symbols, comments, or high-level source.
Neither group executes programs.

Install the tool using [Getting started](getting-started.md). See the
[CLI reference](cli-reference.md) for all commands and [Scripting](scripting.md)
for JSON and exit codes.

## Commands and output handling

| Command | Alias | Input and result |
| --- | --- | --- |
| `asm compile INPUT --to OUTPUT` | `assemble` | UTF-8 assembly to machine code |
| `asm decompile INPUT --to OUTPUT` | `disassemble` | Machine code to UTF-8 assembly |
| `asm listing INPUT --to OUTPUT` | — | Assemble and export source with addresses and bytes |
| `asm map INPUT --to OUTPUT` | — | Assemble and export a versioned JSON symbol/source map |
| `basic compile INPUT --to OUTPUT` | `tokenize` | Numbered UTF-8 listing to Applesoft tokens |
| `basic decompile INPUT --to OUTPUT` | `detokenize` | Applesoft tokens to UTF-8 listing |

| Option | Applies to | Meaning |
| --- | --- | --- |
| `--to PATH` | All; required | Host output; its parent directory must exist |
| `--format raw\|dos` | Host-file commands | Payload or DOS header plus payload; default `raw` |
| `--origin ADDRESS` | All | Decimal, `0x` hex, or shell-quoted `$` hex address |
| `--cpu 6502\|65c02\|w65c02` | Assembly | Instruction set; default `6502` |
| `--from-image IMAGE` | Decompile | Read `INPUT` as an entry path in this image |
| `--input-order dos\|prodos` | With `--from-image` | Override sector layout |
| `--input-fs dos33\|prodos` | With `--from-image` | Override filesystem detection |
| `--overwrite` | All | Permit replacing an existing host output |
| `--json`, `--quiet`, `--verbose` | All | Structured results, suppressed normal text, diagnostic detail |

Use `--help` on any command. Option values such as `65c02` are lowercase.
`--format` cannot accompany `--from-image`; image overrides require it.

Outputs are staged beside their destination and checked by length and SHA-256
before replacement. Invalid input leaves existing output unchanged, even with
`--overwrite`. Output must differ from its source file or image; linked paths
are refused. Host inputs must be regular files. Linux/macOS require
`/usr/bin/stat` to reject pipes, sockets, and devices before opening them.

## Assemble, inspect, and rebuild

From the repository root, with fresh output filenames:

```sh
a2 asm compile examples/hello.asm --to hello.bin
a2 asm decompile hello.bin --origin 0x2000 --to hello.dis.asm
a2 asm compile hello.dis.asm --to rebuilt.bin
```

The [example](../examples/hello.asm) places code at `$2000`, prints `HELLO`
through the monitor's `COUT` entry, and returns. Source supplies `.org`, so
compilation needs no address option. Raw binaries store no load address, so
disassembly requires `--origin`. Check the byte round trip in PowerShell:

```powershell
if ((Get-FileHash hello.bin).Hash -ne (Get-FileHash rebuilt.bin).Hash) {
    throw 'Rebuilt payload differs'
}
```

Disassembly scans sequentially without identifying entry points, data regions,
or control flow. Unknown opcodes and truncated instructions become `.byte`
lines. Listings include `.org`, addresses, and original-byte comments.
Use the **same CPU** to decompile and rebuild; emitted source preserves payload
bytes, including absolute instructions referencing zero-page addresses.

### Processor selection

| CPU | Accepted encodings and additions |
| --- | --- |
| `6502` | 151 documented NMOS 6502 encodings; default for original Apple II and unenhanced IIe code |
| `65c02` | 178 encodings: adds `BRA`, `STZ`, `TSB`, `TRB`, `PHX/PLX`, `PHY/PLY`, accumulator `INC/DEC`, immediate and indexed `BIT`, zero-page indirect ALU/load/store forms, and `JMP (address,X)` |
| `w65c02` | 212 encodings: adds `RMB0`–`RMB7`, `SMB0`–`SMB7`, `BBR0`–`BBR7`, `BBS0`–`BBS7`, `WAI`, and `STP` to the Apple-compatible set |

Use `65c02` for enhanced IIe/IIc instructions; WDC extensions require suitable
hardware. CPU selection is never inferred from a program file or image metadata.
Undocumented opcodes, additional NOP encodings, and 65816 instructions
are unsupported. `BRK` emits one byte; supply a runtime signature byte separately
with `.byte`.

### Source dialect

Mnemonics, directives, and symbols are case-insensitive. Symbol names start with
an ASCII letter or `_`, followed by letters, digits, or `_`. Define labels with
a colon (`loop:`) and constants with `=` (`COUT = $FDED`). Duplicate symbols,
undefined references, and circular constants fail. Semicolons start comments
outside quoted text. Put `.org` before the first label when omitting `--origin`.
Local labels use `@name` and belong to the preceding global label. For example,
`copy: ...`, `@loop: ...`, and `BNE @loop` export the symbol `copy@loop`;
another global routine can define its own `@loop`. Global constants do not change
the local-label scope. Quoted strings retain literal `@` characters.

| Directive | Example | Behavior |
| --- | --- | --- |
| `.org` or `* =` | `.org $2000` | Set address; later forward gaps contain zeroes |
| `.byte` | `.byte $80,'A',"BC"` | Comma-separated byte expressions and ASCII strings |
| `.word` | `.word start,$1234` | Numeric expressions, two little-endian bytes each |
| `.text` | `.text "HELLO","\r"` | Quoted ASCII strings; no added terminator or high bits |
| `.fill` | `.fill 16,$FF` | Repeat a byte count times; omitted value defaults to zero |
| `.align` | `.align 256,$EA` | Advance to a power-of-two boundary in `1..65536`; fill defaults to zero |
| `.assert` | `.assert end-start <= 256,"routine too large"` | Fail if the final expression is zero; optional ASCII message |
| `.include` | `.include "lib/video.asm"` | Insert UTF-8 source relative to the containing file |
| `.incbin` | `.incbin "assets/sprite.bin"` | Insert an entire binary file unchanged |

Output is contiguous. Backward or overlapping origins fail. The initial source
origin must agree with `--origin`; later origins can advance. Origins and fill
counts must resolve during layout; instruction/data operands can reference later
labels. Alignment boundaries resolve during layout; assertions can reference
forward labels and run after layout. Macros, conditional assembly, relocation,
object files, and linking are unsupported by the native assembler.

Includes must occupy their own source line and stay inside the main source
file's directory tree; absolute paths, linked paths, and include cycles are
refused. Source includes can nest to 32 levels across at most 256 distinct
input files. Expanded source keeps the source limits below, and all binary
inclusions together may contain at most 65,536 bytes. Repeated inputs use the
same captured bytes. The CLI checks their hashes before committing output.
The library's `Assembler.AssembleFile` enables includes; `Assembler.Assemble`
accepts source text without filesystem access.

Expressions accept decimal, `$`/`0x` hex, `%` binary, ASCII characters such as
`'A'`, symbols, and `*` for the current address. Unary `+`, `-`, `<` (low
byte), `>` (high byte), and `~` (bitwise complement) bind most tightly.
Binary operators, from highest to lowest precedence, are `* / %`, `+ -`,
`<< >>`, `< <= > >=`, `== !=`, `&`, `^`, and `|`. Operators at the same level
associate left to right. Parentheses override precedence: `LDA #<(message+1)`.
Comparisons produce zero or one. `*` means the current address when an operand
is expected, and multiplication between operands; `%` introduces binary numbers
when an operand is expected, and computes remainder between operands. Division
truncates toward zero. Arithmetic and left shifts reject signed 64-bit overflow;
division by zero and shift counts outside `0..63` fail. Right shift is signed.

For instructions, `*` is the opcode address; in `.byte`/`.word` lists it advances
to each item's address. Constants use their definition's address. Double-quoted
strings support `\n`, `\r`, `\t`, `\0`, `\\`, and `\"`. Character literals
contain one ASCII character without escapes. Use numeric `.byte` values above
`$7F`.

### Addressing modes and operand sizes

Only combinations supported by the instruction and CPU are accepted.

| Mode | Example | Availability |
| --- | --- | --- |
| Implied / accumulator | `RTS`, `ASL A` (or `ASL`) | All |
| Immediate | `LDA #$41` | All |
| Zero page / indexed | `LDA $10`, `LDA $10,X`, `LDX $10,Y` | All |
| Absolute / indexed | `LDA $2000`, `LDA $2000,X`, `LDA $2000,Y` | All |
| Indirect | `JMP ($2000)` | All |
| Indexed indirect | `LDA ($10,X)` | All |
| Indirect indexed | `LDA ($10),Y` | All |
| Relative | `BNE loop` | All; `BRA` requires CMOS |
| Zero-page indirect | `LDA ($10)` | `65c02`, `w65c02` |
| Absolute indexed indirect | `JMP ($2000,X)` | `65c02`, `w65c02` |
| Zero-page relative | `BBR3 $10,loop` | `w65c02` |

Known addresses below `$0100` select zero-page encoding when available. An
unresolved forward address stays absolute when both sizes exist. Force width with
`z:` or `a:`, for example `LDA z:buffer`, `LDA a:$0010`, or `JMP (a:$0010)`.
Writing `$0010` alone does not force absolute encoding.

Byte/immediate/zero-page values must be `0..255`; words/addresses `0..65535`.
Negative bytes are rejected: use `$FF` or `<(-1)` explicitly. Branch targets
are addresses within signed displacement `-128..127` of the following instruction,
accounting for 16-bit wrap. Output itself cannot wrap past `$FFFF`.
Fill counts are `0..65536`, subject to the same output bounds.

Assembly source is bounded to 4 MiB, 100,000 lines, 16,384 characters per line,
and 65,536 symbols. Expressions have a 4,096-character limit, at most 128 binary
operations, and bounded nesting/reference depth.

### Build diagnostics and reports

`asm compile --json` includes `symbols`, `sourceMap`, and `dependencies` in its
result. Symbols contain labels and constants representable as signed 32-bit
integers; larger valid expression constants remain usable but are omitted from
this map. Each source-map entry records the original file and line, address,
byte length, and source text. Zero-length entries describe labels and assertions;
padding belongs to its `.org` or `.align` statement. Binary includes appear as
generated `.byte` rows attributed to the original `.incbin` line. Dependency
values are SHA-256 hashes of the exact file bytes used by the build.

```sh
a2 asm compile examples/hello.asm --to hello.bin --json
a2 asm listing examples/hello.asm --to hello.lst
a2 asm map examples/hello.asm --to hello.map.json
```

`listing` and `map` accept `--origin`, `--cpu`, and `--overwrite`; each writes one
staged report. Listings are diagnostic reports rather than reassemblable source.
Use `asm decompile` to produce reassemblable source from bytes. All three commands
protect their source, include, and binary-input paths against output aliases.
Failures retain the existing output. Assembly errors preserve the legacy
`assembly.invalid_source` error code and include structured diagnostics with
specific codes such as `assembly.undefined_symbol`, `assembly.branch_range`, and
`assembly.assertion_failed`, plus the original file/line and available symbol or
expected/actual values. The assembler reports the first error per invocation.

## Applesoft BASIC

```sh
a2 basic compile examples/hello.bas --to hello.basbin
a2 basic decompile hello.basbin --to hello.list.bas
a2 basic compile hello.list.bas --to rebuilt.basbin
```

The [example](../examples/hello.bas) prints a message and counts from one to three.
Compilation produces interpreter tokens, not machine code. Use decimal line
numbers `0..63999` in strictly increasing order. Blank physical lines are ignored;
bare numbers are rejected because they represent interactive deletion.

### Tokenization rules

| Source example | Behavior |
| --- | --- |
| `10 pr int "Hello"` | Whitespace can occur within keywords; becomes `PRINT` |
| `20 ?"Hello PRINT"` | `?` becomes `PRINT`; string retains spelling |
| `30 REM Print:DATA ?` | Entire `REM` tail remains literal, including colons |
| `40 DATA 1,"a:b",PRINT:PRINT "done"` | DATA is literal until an unquoted colon; following PRINT is tokenized |
| `50 score=1` | Keywords match inside names: `OR` in `score` becomes a token |

Spaces/tabs outside literals are discarded; remaining code letters are
uppercased. Strings, `REM` tails, and `DATA` fields preserve case and whitespace.
Matching follows ROM table order, including `HGR2` before `HGR` and the
`AT`/`ATN` ambiguity rule. Avoid variable names containing keywords.

Source accepts printable ASCII and tabs, UTF-8 with an optional leading BOM,
and CR, LF, or CRLF endings. BASIC source is limited to 1 MiB of characters and
each tokenized body to 250 bytes. Expanded source can exceed the original
interactive keyboard limit. Empty source produces a two-byte terminator.

Decompilation validates contiguous forward links, line order, tokens,
terminators, and absence of trailing bytes. It retokenizes each listing line to
check that its bytes remain unchanged. Noncanonical streams, unsupported high-bit
literal data, Integer BASIC, and custom tokens are rejected. Listings normalize
code formatting and use LF endings. Tokenization is not full syntax checking:
malformed expressions or unterminated strings can tokenize successfully.

### BASIC memory layout and origin

Default origin is `$0801`; permitted origins are `$0100..$FFFE`, with room for
the complete program and its terminator.

| Field | Bytes |
| --- | --- |
| Next line's absolute address (or final terminator address) | 2, little-endian |
| BASIC line number | 2, little-endian |
| Tokenized body | 1–250 |
| Line terminator | `00` |
| After the final line | `00 00` program terminator |

For `10 END` at `$0801`, the raw payload is `07 08 0A 00 80 00 00 00`:
the next pointer is `$0807` and `END` is `$80`. Use matching `--origin`
values when compiling/decompiling elsewhere. **Listings contain no origin
directive**; pass `--origin` again when rebuilding at a nondefault address.
Changing this option on decompilation does not relocate embedded links.

## Host formats and disk integration

Extensions do not select formats. `--format dos` is a host program wrapper:

| Program | Raw format | DOS format |
| --- | --- | --- |
| Machine code | Payload | 2-byte load address + 2-byte payload length + payload |
| Applesoft | Linked token payload | 2-byte payload length + payload; no load address |

Header integers are little-endian. Declared lengths must match exactly; sector
padding is rejected. DOS headers allow at most 65,535 payload bytes; every
program must fit the 16-bit address space. A DOS binary header supplies the
disassembly origin unless `--origin` overrides it.

```sh
a2 asm compile examples/hello.asm --format dos --to hello.dosbin
a2 asm decompile hello.dosbin --format dos --to hello.dos.asm
a2 basic compile examples/hello.bas --format dos --to hello.dosbas
a2 basic decompile hello.dosbas --format dos --to hello.dos.bas
```

Import **raw compiled output** into disk images; disk commands create filesystem
headers themselves. Do not apply text conversion to compiled payloads.

```sh
a2 disk create work.do --fs dos33
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add work.do hello.basbin --name DEMO --type A --in-place
a2 disk create work.po --fs prodos --size 800k
a2 disk add work.po hello.bin --name HELLO --type BIN --aux-type 0x2000 --in-place
a2 disk add work.po hello.basbin --name DEMO --type BAS --aux-type 0x0801 --in-place
a2 asm decompile HELLO --from-image work.po --to from-disk.asm
a2 basic decompile DEMO --from-image work.do --to from-disk.bas
a2 disk verify work.po
```

`--from-image` reads logical payloads, checks DOS B/ProDOS BIN or DOS A/ProDOS
BAS types, and obtains machine-code addresses from metadata. ProDOS BASIC uses
a nonzero auxiliary address; DOS BASIC and zero auxiliary values default to
`$0801`. Explicit `--origin` takes precedence.

After editing and rebuilding, `a2 disk replace work.po HELLO rebuilt.bin --in-place`
updates the payload while retaining the existing file's type and auxiliary address.
Keep that address consistent with the assembly origin.

Use `--from-image` when preservation extraction contains DOS headers and sector
slack; alternatively, `disk export --format binary` produces a logical payload.
See [Disk images](disk-images.md) for manifests, backups, and write semantics.
Created images are data disks without Apple boot code and need an existing
DOS/ProDOS environment to load programs. Emulator execution remains a
[release validation check](VALIDATION.md).

See [Troubleshooting](troubleshooting.md) for failures and
[Development](development.md) for library APIs and tests.

## Format references

Opcode tables are checked against the [MOS MCS6500 programming manual](https://www.bitsavers.org/components/mosTechnology/6500-50A_MCS6500pgmManJan76.pdf),
[Apple IIe Technical Reference, Appendix A](https://www.applelogic.org/files/AIIETECHREF3.pdf),
and [WDC W65C02S datasheet](https://www.westerndesigncenter.com/wdc/documentation/w65c02s.pdf).
Applesoft behavior follows the [ROM token table and tokenizer](https://6502disassembly.com/a2-rom/Applesoft.html).
DOS headers are described in the pinned engine's
[DOS format notes](../third_party/CiderPress2/DiskArc/FS/DOS-notes.md).
