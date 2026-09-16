namespace A2Utils.Core.Projects;

internal static class ProjectRuntimeMemory
{
    internal static IReadOnlyList<MemoryRegion> FromCompiler(Cc65Result compiled, Cc65Options options, ProjectFile file)
    {
        List<MemoryRegion> regions = [];
        foreach (Cc65Segment segment in compiled.Segments.Where(segment => segment.Kind != "header" && segment.Length > 0))
        {
            string bank = options.SegmentBanks.GetValueOrDefault(segment.Name)
                ?? (options.LinkerConfig is null && segment.Name == "LC" ? "lc2" : file.MemoryBank);
            regions.Add(new(segment.Name, segment.Start, segment.Length, bank, segment.Kind));
        }
        foreach (string segment in options.SegmentBanks.Keys)
            if (!compiled.Segments.Any(item => item.Name == segment))
                throw Error("segment_bank", $"Bank declaration refers to missing linker segment '{segment}'.");
        if (compiled.Symbols.TryGetValue("__HIMEM__", out int high) && compiled.Symbols.TryGetValue("__STACKSIZE__", out int stack))
        {
            if (stack < 0 || high < stack || high > 0xc000) throw Error("stack_budget", "Linker software-stack bounds are invalid.");
            if (stack > 0) regions.Add(new("C software stack", high - stack, stack, "main", "stack"));
        }
        return regions;
    }

    internal static List<MemoryRegion> Check(ProjectManifest manifest, IReadOnlyList<(ProjectFile Item, BuiltFile Info)> files)
    {
        TargetProfile profile = TargetProfiles.Get(manifest.Target);
        List<MemoryRegion> builtIn = TargetProfiles.Reserved(manifest.Disk.FileSystem).ToList();
        bool basicSystem = manifest.Runtime == "basic-system" || manifest.Runtime == "auto" &&
            (manifest.Startup is not null || files.Any(file => IsBasic(file.Info.Type)));
        if (manifest.Disk.FileSystem == "prodos" && basicSystem)
            builtIn.Add(new("BASIC.SYSTEM and command buffer", 0x9600, 0x2900));
        MemoryRegion workspace = builtIn.Single(region => region.Name == "zero page, stack, system workspace");
        MemoryRegion display = builtIn.Single(region => region.Name == "display page 1");
        foreach (MemoryRegion reserve in manifest.Reserve) Validate(profile, reserve);
        List<(ProjectFile Item, BuiltFile Info, List<MemoryRegion> Regions)> residents = [];
        foreach (var file in files)
        {
            List<MemoryRegion> regions = [];
            if (file.Info.Origin is { } start)
                regions.Add(new(file.Info.Path, start, file.Info.Length + (IsBasic(file.Info.Type) ? manifest.BasicWorkspaceBytes : 0), file.Info.MemoryBank));
            foreach (MemoryRegion region in file.Info.RuntimeMemory) Validate(profile, region);
            regions.AddRange(file.Info.RuntimeMemory);
            foreach (MemoryRegion region in regions) Validate(profile, region);
            if (!file.Info.Resident) continue;
            if (manifest.CheckMemory)
            {
                int compilerCount = file.Info.RuntimeMemory.Count - file.Item.RuntimeMemory.Count;
                IReadOnlyList<MemoryRegion> compilerRegions = file.Info.RuntimeMemory.Take(compilerCount).ToArray();
                for (int index = 0; index < file.Item.RuntimeMemory.Count; index++)
                {
                    MemoryRegion declared = file.Item.RuntimeMemory[index];
                    bool repeatsCompilerAllocation = compilerRegions.Any(region => region.Start == declared.Start &&
                        region.Length == declared.Length && region.MemoryBank == declared.MemoryBank && region.Kind == declared.Kind);
                    IEnumerable<MemoryRegion> existing = file.Item.RuntimeMemory.Take(index);
                    if (!repeatsCompilerAllocation)
                        existing = existing.Concat(compilerRegions).Concat(file.Info.Origin is { } origin
                            ? [new MemoryRegion("payload", origin, file.Info.Length, file.Info.MemoryBank)] : []);
                    foreach (MemoryRegion other in existing)
                        if (Overlaps(declared, other)) throw Overlap(file.Info.Path + ":" + declared.Name, declared, other);
                }
                foreach (MemoryRegion stack in compilerRegions.Where(region => region.Kind == "stack"))
                    foreach (MemoryRegion other in regions.Where(region => !ReferenceEquals(region, stack) && region.Kind != "stack"))
                        if (Overlaps(stack, other)) throw Overlap(file.Info.Path + ":" + stack.Name, stack, other);
                foreach (MemoryRegion region in regions)
                {
                    foreach (MemoryRegion reserved in builtIn.Concat(manifest.Reserve))
                    {
                        bool loresDisplay = ReferenceEquals(reserved, display) && file.Item.Kind == "lores";
                        // The stock cc65 Apple II runtime saves/restores this specific zero-page workspace.
                        bool compilerZeroPage = ReferenceEquals(reserved, workspace) && file.Item.Kind == "cc65" &&
                            region.Kind == "zero-page" && region.MemoryBank == "main" && region.Start >= 0x80 && region.Start + region.Length <= 0x9a;
                        if (!loresDisplay && !compilerZeroPage && Overlaps(region, reserved))
                            throw Overlap(file.Info.Path, region, reserved);
                    }
                    foreach (var previous in residents)
                    {
                        if (file.Info.OverlayGroup is not null && file.Info.OverlayGroup == previous.Info.OverlayGroup) continue;
                        foreach (MemoryRegion other in previous.Regions)
                            if (Overlaps(region, other)) throw Overlap(file.Info.Path, region, other with { Name = previous.Info.Path + ":" + other.Name });
                    }
                }
            }
            residents.Add((file.Item, file.Info, regions));
        }
        List<MemoryRegion> occupied = [];
        foreach (var resident in residents)
            foreach (var bank in resident.Regions.Where(region => region.Length > 0).GroupBy(region => region.MemoryBank))
            {
                MemoryRegion? merged = null;
                foreach (MemoryRegion region in bank.OrderBy(region => region.Start))
                {
                    if (merged is not null && region.Start <= merged.Start + merged.Length)
                        merged = merged with { Length = Math.Max(merged.Start + merged.Length, region.Start + region.Length) - merged.Start };
                    else
                    {
                        if (merged is not null) occupied.Add(merged);
                        merged = new(resident.Info.Path, region.Start, region.Length, region.MemoryBank);
                    }
                }
                if (merged is not null) occupied.Add(merged);
            }
        return occupied;
    }

    internal static void Validate(TargetProfile profile, MemoryRegion region)
    {
        if (region.Start is < 0 or > 65535 || region.Length < 0 || region.Start > 65536 - region.Length)
            throw Error("memory_range", $"Memory region '{region.Name}' exceeds the address space.");
        TargetMemoryBank bank = profile.MemoryBanks.FirstOrDefault(bank => bank.Name == region.MemoryBank)
            ?? throw Error("memory_bank", $"Memory bank '{region.MemoryBank}' is unavailable on target '{profile.Name}'.");
        if (region.Start < bank.Start || region.Start > bank.Start + bank.Length - region.Length)
            throw Error("memory_bank_range", $"Memory region '{region.Name}' must fit {bank.Name}: [${bank.Start:X4}, ${bank.Start + bank.Length:X5}).");
        if (region.Kind == "zero-page" && (region.MemoryBank is not ("main" or "aux") || region.Start + region.Length > 0x100))
            throw Error("zero_page", $"Zero-page allocation '{region.Name}' must fit $0000-$00FF in main or aux RAM.");
    }

    private static bool IsBasic(string type) => Backends.DiskSession.ParseFileType(type) == 0xfc;

    private static bool Overlaps(MemoryRegion first, MemoryRegion second)
    {
        int start = Math.Max(first.Start, second.Start);
        int end = Math.Min(first.Start + first.Length, second.Start + second.Length);
        if (start >= end) return false;
        if (first.MemoryBank == second.MemoryBank) return true;
        bool sharedUpper = (first.MemoryBank, second.MemoryBank) is
            ("lc1", "lc2") or ("lc2", "lc1") or ("aux-lc1", "aux-lc2") or ("aux-lc2", "aux-lc1");
        return sharedUpper && end > Math.Max(start, 0xe000);
    }

    private static DiskException Overlap(string owner, MemoryRegion region, MemoryRegion other) =>
        new("project.memory_overlap", $"'{owner}' ({region.MemoryBank}) overlaps '{other.Name}' ({other.MemoryBank}).", 2)
        {
            Diagnostics = [new("project.memory_overlap", "error", "Resident memory ranges overlap.", Symbol: owner,
                Expected: $"Outside {other.Name} {other.MemoryBank}:[${other.Start:X4}, ${other.Start + other.Length:X5})",
                Actual: $"{region.MemoryBank}:[${region.Start:X4}, ${region.Start + region.Length:X5})")]
        };

    private static DiskException Error(string code, string message) => new("project." + code, message, 2);
}
