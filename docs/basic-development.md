# BASIC source checks and renumbering

`basic compile` remains a ROM-compatible tokenizer. Use the opt-in source checker
before compiling to catch common mistakes in generated or edited Applesoft code:

```sh
a2 basic check examples/hello.bas --json
a2 basic renumber examples/hello.bas --start 100 --step 10 --to hello.renumbered.bas --json
```

`basic check` reports a `basic.check` result envelope with `data.valid` and a
`data.diagnostics` array on stdout. It exits 0 when no errors are found and 2
when checks find errors. A completed check with errors still uses a stdout
result envelope; an I/O or invocation failure uses the usual stderr error
envelope. Diagnostics include severity, code, file, physical source line,
column, and BASIC line number when available. Warnings alone do not fail the
check. Check the exit status as well as the JSON result.

The checker validates the tokenizer's source constraints, literal destinations
of GOTO, GOSUB, THEN, ON ... GOTO/GOSUB, ONERR GOTO, and RUN, and common
expression syntax in assignments, PRINT, IF, FOR, and ON. It reports missing
destinations as errors even in branches that might not run. Computed targets
receive a warning because their destinations cannot be established statically.
Strings, DATA fields, and REM tails are not interpreted as statements.

Different variable spellings with the same first two significant characters
produce advisory collision warnings; string, integer, numeric, and array
identities are distinguished. An assignment name containing a ROM keyword
also receives an explanatory warning; its resulting token stream may produce
a syntax error. A string extending to the physical line end is advisory:
Applesoft accepts the implicit closing quote, but an explicit quote is easier
to review.

This is a conservative development checker, not a complete Applesoft grammar,
type checker, or interpreter. Other statements receive only delimiter checks.
It checks built-in function arity (including optional MID$ length), literal
negative subscripts, and a small set of certain numeric domain errors such as
`SQR(-1)`, `LOG(0)`, and `CHR$(256)`. Lexical FOR/NEXT pairing, GOSUB/RETURN
context, and literal subscripts exceeding a preceding DIM's inclusive bounds
produce advisory warnings: branches and external callers can change the runtime
context. Computed dimensions and subscripts are not guessed. It does not prove
runtime success, variable initialization, general numeric ranges, dynamic array
bounds, or the behavior of CALL/POKE.
Exercise the program in an Apple II environment as well.

Project executions can opt into `checkBasicRuntime` to recognize Applesoft
`?… ERROR IN n` messages on the captured text screen. Diagnostics map the BASIC
line through the build's source map to the original file and physical source
line, including labeled source. If several BASIC programs share that line
number, the diagnostic reports ambiguity rather than choosing a source file.
This detection is opt-in because a program can intentionally print the same
text. Errors that have scrolled off the captured screen cannot be recovered.

Renumbering changes numbered line prefixes and supported literal branch targets
together. It preserves the rest of the source, including strings, DATA, REM,
blank lines, and line endings. The output is UTF-8 without a BOM. The JSON
result includes `outputPath`, `mapping` entries with `sourceLine`, `oldLine`,
and `newLine`, and advisory diagnostics. These mappings let an editor or agent
relate rewritten BASIC line numbers to the original source.

The start must be 0..63999, the step positive, and every generated number must
fit in 0..63999. Input must already have strictly increasing, unique line
numbers. Missing destinations, detected syntax errors, computed targets, and
LIST/DEL ranges are refused rather than rewritten speculatively. Numeric
constants outside branch-target positions are left untouched. Source labels
and unnumbered source are not supported by this command.

`--to` must identify a separate host file. Existing outputs require
`--overwrite`; the source is never replaced. Rewrites are checked for tokenized
line and program size limits, staged, and validated before replacement, so a
failed renumber leaves an existing output intact.

## Symbolic source

Use `basic prepare` for unnumbered source with explicit labels:

```basic
@start: HOME
FOR I=1 TO 3
GOSUB @show
NEXT I
GOTO @done

@show:
PRINT "HELLO ";I
RETURN

@done: END
```

```sh
a2 basic prepare examples/development/labels.bas --start 10 --step 10 --to labels.numbered.bas --json
a2 basic compile labels.numbered.bas --to labels.bin
```

Labels start with `@`, end with `:`, and must appear at the beginning of a
physical line. A standalone label attaches to the next statement; multiple
labels may name the same statement. Names are case insensitive, contain 1..64
ASCII letters, digits, or underscores, and cannot begin with a digit.

References such as `GOTO @start`, `GOSUB @show`, `IF X THEN @done`,
`IF X GOTO @done`, and `ON X GOTO @first,@second` expand to ordinary line
numbers. `ONERR GOTO @handler` also works. References must occupy an entire
literal branch-target position: using labels in arithmetic, assignments, PRINT,
RUN, or other expressions is refused. Strings, DATA fields, and REM comments
retain literal `@` text without resolving it.

The output uses LF line endings and UTF-8 without a BOM. Blank source lines and
standalone label declarations do not consume BASIC line numbers. The remaining
statement text, including text within strings, DATA, and REM, is preserved.
`data.mapping` reports each emitted statement's original `sourceLine`, generated
`basicLine`, and `labels`. Label diagnostics point to their original source
locations; expression diagnostics identify the original statement line and its
start column after expansion. The source checker runs before output is staged.

Source is limited to 1 MiB and 16384 characters per physical line before expansion.
Duplicate or unresolved labels, labels without a following statement, numbered
input, invalid contexts, oversized programs, and numbers outside 0..63999 are
refused. `--to` must be a separate file; existing output requires `--overwrite`.
Failure leaves both the source and any existing output unchanged.
