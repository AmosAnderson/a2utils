; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; Original one-sector Disk II boot program. No operating-system files are used.
; The Disk II firmware loads sector zero at $0800 and enters at $0801.
.org $0800
.byte 1
lda #0
sta $0300
sta $0301
lda #$c1
sta $0400
lda #$b2
sta $0401
lda #$a0
sta $0402
lda #$d0
sta $0403
lda #$c1
sta $0404
lda #$d3
sta $0405
sta $0406
bit $c010
wait: lda $c000
bpl wait
bit $c010
cmp #$d8
bne wait
sta $0301
lda #$2a
sta $0300
done: jmp done
