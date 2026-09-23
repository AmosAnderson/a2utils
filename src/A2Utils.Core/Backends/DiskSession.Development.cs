// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using DiskArc;
using static DiskArc.Defs;

namespace A2Utils.Core.Backends;

public sealed partial class DiskSession
{
    /// <summary>Sets explicit dates on a staged image for reproducible project builds.</summary>
    public void SetTimestamps(string path, DateTime created, DateTime modified)
    {
        EnsureWritable();
        IFileEntry entry = Resolve(path);
        RequireAccess(entry, AccessFlags.Write);
        Mutate(() =>
        {
            entry.CreateWhen = created;
            entry.ModWhen = modified;
            entry.SaveChanges();
        });
    }
}
