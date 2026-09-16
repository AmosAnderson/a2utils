; Original BASIC CALL routine: adds two unsigned bytes, returning a 16-bit sum.
; Load at $6000; caller reserves $6000-$601F code and $6040-$6043 mailbox.
; Inputs $6040,$6041; outputs low $6042,high $6043. Preserves X,Y and P;
; clobbers A. No zero-page, ROM, soft-switch, or OS access.
.org $6000
a2_add_bytes:
    PHP
    CLD
    LDA $6040
    CLC
    ADC $6041
    STA $6042
    LDA #0
    ADC #0
    STA $6043
    PLP
    RTS
