; BRUN STRIPES. Fill HGR page 1 with a visible stripe pattern; any key restores text.
; The program lives at $6000, above the $2000-$3FFF display page it changes.
.include "apple2.inc"
.org $6000
start:
    LDA #$55
    LDY #0
@fill:
    STA $2000,Y
    STA $2100,Y
    STA $2200,Y
    STA $2300,Y
    STA $2400,Y
    STA $2500,Y
    STA $2600,Y
    STA $2700,Y
    STA $2800,Y
    STA $2900,Y
    STA $2A00,Y
    STA $2B00,Y
    STA $2C00,Y
    STA $2D00,Y
    STA $2E00,Y
    STA $2F00,Y
    STA $3000,Y
    STA $3100,Y
    STA $3200,Y
    STA $3300,Y
    STA $3400,Y
    STA $3500,Y
    STA $3600,Y
    STA $3700,Y
    STA $3800,Y
    STA $3900,Y
    STA $3A00,Y
    STA $3B00,Y
    STA $3C00,Y
    STA $3D00,Y
    STA $3E00,Y
    STA $3F00,Y
    INY
    BNE @fill
    BIT KBDSTRB
    BIT HIRES
    BIT PAGE1
    BIT MIXCLR
    BIT TXTCLR
@key:
    LDA KBD
    BPL @key
    BIT KBDSTRB
    BIT TXTSET
    RTS
.assert *-start < 256,"graphics routine exceeds one page"
