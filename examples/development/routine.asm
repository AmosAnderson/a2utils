; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; Called by mixed.bas. Load address and CALL operand must agree.
.include "apple2.inc"
.org $2000
start:
    LDX #0
@print:
    LDA message,X
    BEQ @done
    ORA #$80
    JSR COUT
    INX
    BNE @print
@done:
    RTS
message:
    .text "HELLO FROM MACHINE CODE"
    .byte $0D,0
