; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; Original 6502 routines for a 128 KiB Apple IIe or IIc.
; Execute from MAIN RAM with RAMRD=0 and ALTZP=0; no NMI may occur.
; $06/$07 = source pointer in main RAM, $08/$09 = destination pointer.
; Copies exactly 256 bytes. Caller owns these zero-page locations.
; Both routines preserve X and processor flags; A and Y are clobbered.
; IRQs are disabled during the copy. No ROM/OS calls occur while banked.

; Source and destination ranges must fit $0200-$BFFF.
; Preserves RAMWRT and 80STORE. RAMRD and ALTZP remain unchanged.
copy_page_to_aux:
    PHP
    SEI
    LDA $C018
    PHA
    LDA $C014
    PHA
    STA $C000
    STA $C005
    LDY #0
@copy:
    LDA ($06),Y
    STA ($08),Y
    INY
    BNE @copy
    STA $C004
    PLA
    BPL @restore_store
    STA $C005
@restore_store:
    PLA
    BPL @done
    STA $C001
@done:
    PLP
    RTS

; Source fits $0200-$BFFF; destination fits $D000-$FFFF.
; Requires RAMWRT=0. 80STORE must not redirect the source to aux memory.
; Returns with ROM readable, LC writes disabled, and LC bank 2 selected.
; Changes MAIN LC bank 1; $E000-$FFFF is shared with LC bank 2.
; For DOS/custom runtimes only: ProDOS occupies this language-card RAM.
copy_page_to_lc1:
    PHP
    SEI
    BIT $C082
    BIT $C08B
    BIT $C08B
    LDY #0
@copy:
    LDA ($06),Y
    STA ($08),Y
    INY
    BNE @copy
    BIT $C082
    PLP
    RTS
