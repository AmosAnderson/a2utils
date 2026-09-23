; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; Original 6502 building blocks. Main RAM/ROM mapped, ALTZP=0.
; a2_puts: $06/$07 points to a NUL-terminated ASCII string of at most 255 bytes.
; Outputs through the monitor COUT vector (and thus the active DOS output hook).
; Clobbers A/X/Y/P and $08; leaves $06/$07 unchanged. Does not change text mode.
a2_puts:
    LDY #0
@next:
    LDA ($06),Y
    BEQ @done
    ORA #$80
    STY $08
    JSR $FDED
    LDY $08
    INY
    BNE @next
@done:
    RTS

; a2_getkey: nonblocking. C=1 and A=7-bit key when ready; otherwise C=0,A=0.
; A pending key is consumed by clearing its strobe. Preserves X,Y; clobbers A,P.
a2_getkey:
    LDA $C000
    BPL @empty
    AND #$7F
    BIT $C010
    SEC
    RTS
@empty:
    LDA #0
    CLC
    RTS

; a2_home: clear the active 40-column monitor text window. ROM mapping required.
; Use the active 80-column firmware's HOME from BASIC for an 80-column window.
a2_home:
    JMP $FC58
