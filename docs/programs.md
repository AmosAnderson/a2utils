# Apple II program tools

The `asm` and `basic` groups compile UTF-8 source files and decompile host
programs or image entries. They are native C# library operations with no
external assembler dependency. All host outputs are staged, verified, and
committed atomically; existing files require `--overwrite`. An output cannot
replace its input source or disk image. `--json`, `--quiet`, and `--verbose`
work with both groups.

Host inputs must be regular files. Linux and macOS use `/usr/bin/stat` to
reject pipes, sockets, and devices before opening them.

## Assembly and machine code

```sh
a2 asm compile examples/hello.asm --to hello.bin
a2 asm decompile hello.bin --origin 0x2000 --to hello.dis.asm
a2 asm compile hello.dis.asm --to rebuilt.bin
a2 asm compile examples/hello.asm --format dos --to hello.dosbin
a2 asm decompile hello.dosbin --format dos --to restored.asm
```

`assemble` aliases `compile`; `disassemble` aliases `decompile`. Select the
processor explicitly when using extensions:

| `--cpu` | Instructions |
| --- | --- |
| `6502` (default) | 151 documented NMOS 6502 encodings |
| `65c02` | 178 Apple-compatible encodings for enhanced IIe/IIc |
| `w65c02` | Adds RMB/SMB/BBR/BBS, WAI, and STP for newer WDC hardware |

The assembler accepts case-insensitive mnemonics and labels, `label:`, constants
such as `COUT = $FDED`, and `;` comments. Supported directives are `.org`,
`.byte`, `.word`, `.text`, and `.fill count[,value]`; `* = address` also sets
the origin. Text is ASCII. `.word` stores little-endian values. Later `.org`
directives fill forward gaps with zeroes; backward or overlapping origins fail.

Expressions support decimal, `$`/`0x` hexadecimal, `%` binary, labels, constants,
`*` for the current address, parentheses, addition/subtraction, and unary `<`
and `>` for low/high bytes. Example: `LDA #<message`. Forward references are
allowed for operands; origins and fill counts must resolve during layout.
Source requires `.org` or `--origin`; conflicting initial values are rejected.

Resolved addresses below `$0100` use zero-page instructions where available.
Forward unresolved addresses retain absolute encoding where available, keeping
layout deterministic. Prefix an operand with `z:` or `a:` to force its width,
for example `LDA z:buffer` or `LDA a:$0010`. Branch distances and all operand
widths are checked. No includes, macros, object files, or linking are supported.

Disassembly scans bytes sequentially and includes addresses and original bytes
as comments. Unknown opcodes and incomplete instructions become `.byte` lines.
Using the same CPU, its listing reassembles to identical payload bytes,
including absolute instructions that reference zero-page addresses. It does
not infer entry points, data regions, symbols, or control flow, and cannot
recover original high-level source or comments. `BRK` emits one opcode byte;
its runtime signature byte must be supplied separately with `.byte`.

## Applesoft BASIC

```sh
a2 basic compile examples/hello.bas --to hello.basbin
a2 basic decompile hello.basbin --to hello.list.bas
a2 basic compile examples/hello.bas --format dos --to hello.dosbas
a2 basic decompile hello.dosbas --format dos --to hello.list.bas --overwrite
```

`tokenize` and `detokenize` are aliases. Compilation creates tokenized Applesoft
programs for the interpreter, rather than native machine code. Use strictly
increasing numbered lines from 0 through 63999. Keywords are case-insensitive;
`?` abbreviates `PRINT`. Strings, `REM` comments, and `DATA` fields preserve
literal text. Code whitespace is normalized using Applesoft tokenization rules.
The default memory origin is `$0801`; use matching `--origin` values for
programs stored elsewhere.

Source uses printable ASCII and tabs. Limits are 1 MiB of BASIC source,
250 tokenized body bytes per line, and origins from `$0100` through `$FFFE`;
the complete program must fit before the end of memory. Expanded source lines
may exceed the original keyboard input limit when their tokens fit.

Decompilation validates line pointers, terminators, line order, and tokens.
Listings preserve representable tokenized contents, not the original source
formatting. Noncanonical token streams that cannot survive listing and
retokenization are rejected. The tokenizer does not execute programs or
validate every expression's runtime grammar. Integer BASIC and custom BASIC
extensions are unsupported.

## Program files and disk images

Raw host files contain payload bytes only. DOS binary host files add a
little-endian load address and length (four bytes); DOS BASIC host files add
only a length (two bytes). `--format dos` requires the declared length to match
exactly and rejects sector padding. Formats are explicit; filename extensions
do not select one. Programs must fit the 16-bit address space. Raw binary
disassembly requires `--origin`, while DOS binary headers supply it.

Use raw output with existing disk commands; they create filesystem headers:

```sh
a2 disk create work.do --fs dos33
a2 disk add work.do hello.bin --name HELLO --type B --load-address 0x2000 --in-place
a2 disk add work.do hello.basbin --name DEMO --type A --in-place
a2 disk create work.po --fs prodos --size 800k
a2 disk add work.po hello.bin --name HELLO --type BIN --aux-type 0x2000 --in-place
a2 disk add work.po hello.basbin --name DEMO --type BAS --aux-type 0x0801 --in-place
a2 asm decompile HELLO --from-image work.po --to from-disk.asm
a2 basic decompile DEMO --from-image work.do --to from-disk.bas
```

`--from-image` reads logical contents directly and checks the entry's file type:
DOS B/ProDOS BIN for assembly, DOS A/ProDOS BAS for Applesoft. Machine-code load
addresses come from metadata. ProDOS BASIC uses a nonzero auxiliary address;
DOS BASIC and zero auxiliary addresses default to `$0801`. An explicit
`--origin` overrides these defaults. Image layout overrides `--input-order`
and `--input-fs` are available here. Do not combine `--from-image` and `--format`.

The examples are original source, with no Apple boot code. Generated data
disks require an existing DOS/ProDOS environment to load programs. Emulator
execution remains a release check; compiling successfully is not proof that
an arbitrary program is safe or correct to run.

## Format references

Opcode tables are checked against the [MOS MCS6500 programming manual](https://www.bitsavers.org/components/mosTechnology/6500-50A_MCS6500pgmManJan76.pdf),
[Apple IIe Technical Reference, Appendix A](https://www.applelogic.org/files/AIIETECHREF3.pdf),
and [WDC W65C02S datasheet](https://www.westerndesigncenter.com/wdc/documentation/w65c02s.pdf).
Applesoft behavior follows the [ROM token table and tokenizer](https://6502disassembly.com/a2-rom/Applesoft.html).
DOS header structure is described in the pinned engine's
[DOS format notes](../third_party/CiderPress2/DiskArc/FS/DOS-notes.md).
