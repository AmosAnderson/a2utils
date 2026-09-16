.org $6000
.include "generated/atlas.inc"
start:
    CLD
    BIT $C050
    BIT $C052
    BIT $C054
    BIT $C057
    LDA #<sprites
    STA $06
    LDA #>sprites
    STA $07
    LDA #20
    STA $0A
    LDA #90
    STA $0B
    LDA #ATLAS_BYTES_PER_ROW
    STA $0C
    LDA #ATLAS_CELL_HEIGHT
    STA $0D
    JSR a2_hgr_sprite
    RTS
.include "hgr-sprite.inc"
sprites:
.incbin "generated/atlas.bin"
