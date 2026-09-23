; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; DOS/custom-runtime demonstration. Leaves the normal main/ROM mapping.
; Clobbers A/X/Y, $06-$09, $0300-$0302, main $3000-$30FF and $4000,
; aux $4000-$40FF, lc1 $D000-$D0FF, and lc2 $D000.
    JMP start
.include "bank-copy.inc"
start:
    PHP
    SEI
    STA $C000
    STA $C002
    STA $C004
    STA $C008
    LDX #0
@fill:
    TXA
    STA $3000,X
    INX
    BNE @fill
    LDA #$A5
    STA $4000
    BIT $C083
    BIT $C083
    LDA #$A6
    STA $D000
    BIT $C082
    LDA #0
    STA $06
    STA $08
    LDA #$30
    STA $07
    LDA #$40
    STA $09
    STA $C001
    JSR copy_page_to_aux
    LDA $C018
    AND #$80
    STA $0301
    LDA $C014
    AND #$80
    STA $0302
    STA $C000
    LDA #$D0
    STA $09
    JSR copy_page_to_lc1
    LDA #$2A
    STA $0300
    PLP
    RTS
