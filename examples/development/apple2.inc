; SPDX-FileCopyrightText: 2026 Amos Anderson
; SPDX-License-Identifier: GPL-2.0-only

; Baseline Apple II Plus/IIe/IIc monitor and I/O addresses.
; Read/write side effects depend on the selected machine and memory configuration.
COUT = $FDED
HOME = $FC58
KBD = $C000
KBDSTRB = $C010
TXTCLR = $C050
TXTSET = $C051
MIXCLR = $C052
PAGE1 = $C054
HIRES = $C057
