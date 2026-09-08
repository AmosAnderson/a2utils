; Prints HELLO through the Apple II monitor's COUT routine, then returns.
.org $2000
COUT = $FDED
    LDX #0
loop:
    LDA message,X
    BEQ done
    ORA #$80
    JSR COUT
    INX
    BNE loop
done:
    RTS
message:
    .text "HELLO"
    .byte $0D,0
