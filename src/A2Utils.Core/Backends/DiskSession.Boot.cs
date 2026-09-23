// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using DiskArc;

namespace A2Utils.Core.Backends;

public sealed partial class DiskSession
{
    /// <summary>Writes complete 256-byte boot sectors on track zero of a 140 KiB sector image.</summary>
    public void WriteBootSectors(byte[] sectors)
    {
        ArgumentNullException.ThrowIfNull(sectors);
        EnsureWritable();
        if (_disk.ChunkAccess is not { HasSectors: true, FormattedLength: 143360 } chunks ||
            sectors.Length is < 256 or > 4096 || sectors.Length % 256 != 0)
            throw new DiskException("boot.geometry", "Boot sectors require 1..16 complete sectors on a 140 KiB sector image.", 3);
        Mutate(() =>
        {
            for (int sector = 0; sector < sectors.Length / 256; sector++)
                chunks.WriteSector(0, (uint)sector, sectors, sector * 256, Defs.SectorOrder.DOS_Sector);
        });
    }

    /// <summary>Reads complete 256-byte sectors from track zero in DOS logical order.</summary>
    public byte[] ReadBootSectors(int count)
    {
        if (_disk.ChunkAccess is not { HasSectors: true, FormattedLength: 143360 } chunks ||
            count is < 1 or > 16)
            throw new DiskException("boot.geometry", "Boot sectors require 1..16 sectors on a 140 KiB sector image.", 3);
        return ReadEngine(() =>
        {
            byte[] result = new byte[count * 256];
            for (int sector = 0; sector < count; sector++)
                chunks.ReadSector(0, (uint)sector, result, sector * 256, Defs.SectorOrder.DOS_Sector);
            return result;
        });
    }
}
