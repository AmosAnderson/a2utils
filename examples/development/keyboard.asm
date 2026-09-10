; BRUN KEYECHO. Echo keys through the monitor; Return exits to the caller.
.include "apple2.inc"
.org $2000
start:
    JSR HOME
@read:
    LDA KBD
    BPL @read
    BIT KBDSTRB
    CMP #$8D
    BEQ @done
    JSR COUT
    JMP @read
@done:
    LDA #$8D
    JSR COUT
    RTS
.assert *-start < 256,"keyboard routine exceeds one page"
