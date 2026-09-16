; Original ProDOS 8 MLI wrappers. ProDOS must be initialized; MAIN RAM/ROM and
; ALTZP=0. Keep code in writable main RAM below $BF00; not reentrant.
; All entries take X=parameter-list low byte, Y=high byte.
; Return the MLI's C flag and A error code unchanged. Treat X/Y/P as clobbered.
; OPEN ($C8): 3,pathLo,pathHi,bufferLo,bufferHi,refNum(result).
; Buffer is a caller-reserved, page-aligned 1024 bytes. Path is length-prefixed.
; READ/WRITE ($CA/$CB): 4,refNum,dataLo,dataHi,countLo,countHi,doneLo,doneHi.
; Always inspect doneLo/Hi; READ at EOF may transfer fewer bytes.
; CLOSE ($CC): 1,refNum. Pass a nonzero refNum to avoid closing other files.
; Existing-file I/O only: CREATE, SET_EOF and error policy remain caller choices.
a2_file_open:
    LDA #$C8
    JMP a2_file_call
a2_file_read:
    LDA #$CA
    JMP a2_file_call
a2_file_write:
    LDA #$CB
    JMP a2_file_call
a2_file_close:
    LDA #$CC
a2_file_call:
    STA a2_file_command
    STX a2_file_parameters
    STY a2_file_parameters+1
    JSR $BF00
a2_file_command:
    .byte 0
a2_file_parameters:
    .word 0
    RTS
