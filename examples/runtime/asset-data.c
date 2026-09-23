/* SPDX-FileCopyrightText: 2026 Amos Anderson */
/* SPDX-License-Identifier: GPL-2.0-only */

/* A generated header is a compile-time input, requiring no host generated file.
 * Include the header in a single C translation unit to avoid duplicating data.
 * Select this as a project c source with cc65 and the same asset step.
 */
#include "generated/atlas.h"
#include <stdio.h>

unsigned char first_sprite_row(void)
{
    return ATLAS_DATA[ATLAS_CELL_0_OFFSET];
}

int main(void)
{
    printf("CELLS %lu, BYTES %lu, FIRST ROW %u\n",
        ATLAS_CELL_COUNT, ATLAS_LENGTH, (unsigned int)first_sprite_row());
    return 0;
}
