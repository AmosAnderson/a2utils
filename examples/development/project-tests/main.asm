; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: MIT

.org $2000
RESULT = $0300
start: lda #$2a
sta RESULT
rts
