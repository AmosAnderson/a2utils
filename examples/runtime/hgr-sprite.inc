; Original 6502 opaque HGR page-1 sprite blitter. Main RAM, RAMRD/RAMWRT=0,
; ALTZP=0, 80STORE=0; caller selects the display mode. Decimal mode must be off.
; $06/$07 source pointer: contiguous rows, WIDTH bytes per source row.
; $0A byte column 0..39 (pixel x = column*7); $0B pixel row 0..191.
; $0C WIDTH 1..40 bytes; $0D HEIGHT 1..192 rows.
; Seven-bit LSB-first asset rows; bit 7 should be clear for monochrome packing.
; Replaces whole bytes, including padding: no transparency or pixel shifting.
; Clips the right and bottom edges. Invalid/empty geometry draws nothing.
; Clobbers A,Y,P,$06-$10; preserves X. $06/$07 advances by full source rows;
; $0B advances by rendered rows. Source must not overlap screen/destination/code.
a2_hgr_sprite:
    LDA $0A
    CMP #40
    BCS @return
    LDA $0B
    CMP #192
    BCS @return
    LDA $0C
    BEQ @return
    CMP #41
    BCS @return
    LDA $0D
    BEQ @return
    CMP #193
    BCS @return
    STA $0E
    LDA #40
    SEC
    SBC $0A
    CMP $0C
    BCC @width
    LDA $0C
@width:
    STA $0F
    JMP @row
@return:
    RTS
@row:
    LDA $0B
    CMP #192
    BCS @return
    AND #7
    ASL A
    ASL A
    ORA #$20
    STA $09
    LDA #0
    STA $08
    LDA $0B
    AND #$38
    LSR A
    LSR A
    LSR A
    LSR A
    ROR $08
    CLC
    ADC $09
    STA $09
    LDA $0B
    AND #$C0
    LSR A
    LSR A
    LSR A
    STA $10
    ASL A
    ASL A
    CLC
    ADC $10
    CLC
    ADC $08
    CLC
    ADC $0A
    STA $08
    LDY #0
@column:
    LDA ($06),Y
    STA ($08),Y
    INY
    CPY $0F
    BNE @column
    LDA $06
    CLC
    ADC $0C
    STA $06
    BCC @next
    INC $07
@next:
    INC $0B
    DEC $0E
    BEQ @return
    JMP @row
