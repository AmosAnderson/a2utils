using A2Utils.Core.Assembly;

namespace A2Utils.Core.Projects;

public sealed record PlatformSymbol(string Name, int Address, string Description);
public sealed record TargetMemoryBank(string Name, int Start, int Length, string? SharedUpperBank = null)
{
    public int? SharedUpperStart => SharedUpperBank is null ? null : 0xe000;
}
public sealed record TargetProfile(string Name, string Description, string Cpu, int MainMemoryBytes)
{
    public int BankedMainMemoryBytes { get; init; }
    public int AuxiliaryMemoryBytes { get; init; }
    public string? AuxiliaryMemoryRequirement { get; init; }
    public bool Supports80ColumnText { get; init; }
    public bool SupportsMouseText { get; init; }
    public IReadOnlyList<TargetMemoryBank> MemoryBanks { get; init; } = [new("main", 0, 0xc000)];
}

public static class TargetProfiles
{
    public static IReadOnlyList<TargetProfile> All { get; } = [
        new("apple2plus", "Apple II Plus, 48 KiB main RAM, Applesoft ROM", "6502", 49152),
        IieProfile("apple2e", "Unenhanced Apple IIe", "6502", false),
        IieProfile("apple2enh", "Enhanced Apple IIe", "65c02", true),
        IieProfile("apple2c", "Apple IIc", "65c02", true)
    ];

    public static IReadOnlyList<PlatformSymbol> Symbols { get; } = [
        new("COUT", 0xfded, "Monitor character output; character in A, high bit set for normal text."),
        new("HOME", 0xfc58, "Monitor clear text window."),
        new("KBD", 0xc000, "Read keyboard latch; bit 7 signals a pending key."),
        new("KBDSTRB", 0xc010, "Access clears the keyboard strobe."),
        new("SPEAKER", 0xc030, "Access toggles speaker output."),
        new("TXTCLR", 0xc050, "Access selects graphics display."),
        new("TXTSET", 0xc051, "Access selects text display."),
        new("MIXCLR", 0xc052, "Access selects full-screen graphics."),
        new("MIXSET", 0xc053, "Access selects mixed text/graphics."),
        new("PAGE1", 0xc054, "Access selects display page 1 in the baseline memory configuration."),
        new("PAGE2", 0xc055, "Access selects display page 2 in the baseline memory configuration."),
        new("LORES", 0xc056, "Access selects low-resolution graphics."),
        new("HIRES", 0xc057, "Access selects high-resolution graphics."),
        new("STORE80OFF", 0xc000, "IIe/IIc: write disables PAGE2 overrides of RAMRD/RAMWRT for screen memory."),
        new("STORE80ON", 0xc001, "IIe/IIc: write enables PAGE2 selection of main/aux text page 1 and, when HIRES is on, hi-res page 1."),
        new("RAMRDOFF", 0xc002, "IIe/IIc: write selects main RAM reads at $0200-$BFFF, subject to 80STORE."),
        new("RAMRDON", 0xc003, "IIe/IIc: write selects auxiliary RAM reads at $0200-$BFFF, subject to 80STORE."),
        new("RAMWRTOFF", 0xc004, "IIe/IIc: write selects main RAM writes at $0200-$BFFF, subject to 80STORE."),
        new("RAMWRTON", 0xc005, "IIe/IIc: write selects auxiliary RAM writes at $0200-$BFFF, subject to 80STORE."),
        new("ALTZPOFF", 0xc008, "IIe/IIc: write selects main zero page, stack and language-card RAM."),
        new("ALTZPON", 0xc009, "IIe/IIc: write selects auxiliary zero page, stack and language-card RAM."),
        new("COL80OFF", 0xc00c, "IIe/IIc: write selects 40-column text."),
        new("COL80ON", 0xc00d, "IIe/IIc: write selects 80-column text; auxiliary display RAM is required."),
        new("ALTCHAROFF", 0xc00e, "IIe/IIc: write selects the primary character set."),
        new("ALTCHARON", 0xc00f, "IIe/IIc: write selects alternate characters; MouseText requires an enhanced IIe or IIc."),
        new("LCBANK2READ", 0xc080, "Read selects language-card bank 2 RAM for reads and disables writes."),
        new("LCROM", 0xc082, "Read selects ROM and disables language-card writes."),
        new("LCBANK2WRITE", 0xc083, "Read twice enables language-card bank 2 reads and writes."),
        new("LCBANK1READ", 0xc088, "Read selects language-card bank 1 RAM for reads and disables writes."),
        new("LCBANK1WRITE", 0xc08b, "Read twice enables language-card bank 1 reads and writes."),
        new("PRODOS_MLI", 0xbf00, "ProDOS MLI entry; inline command byte and parameter-block pointer after JSR.")
    ];

    public static TargetProfile Get(string name) => All.FirstOrDefault(profile => profile.Name == name)
        ?? throw new DiskException("project.target", $"Unknown target '{name}'. Use a2 targets --json.", 2);

    public static CpuKind ParseCpu(string name) => name switch
    {
        "6502" => CpuKind.Mos6502,
        "65c02" => CpuKind.Apple65C02,
        "w65c02" => CpuKind.Wdc65C02,
        _ => throw new DiskException("project.cpu", $"Unsupported CPU '{name}'.", 2)
    };

    public static IReadOnlyList<MemoryRegion> Reserved(string runtime) => [
        new("zero page, stack, system workspace", 0, 0x400),
        new("display page 1", 0x400, 0x400),
        new(runtime == "dos33" ? "DOS workspace" : "ProDOS global page",
            runtime == "dos33" ? 0x9600 : 0xbf00, runtime == "dos33" ? 0x2a00 : 0x100),
        .. runtime == "prodos" ? new MemoryRegion[] {
            new("ProDOS language-card bank 1", 0xd000, 0x3000, "lc1"),
            new("ProDOS language-card bank 2 and dispatcher", 0xd000, 0x1000, "lc2")
        } : []
    ];

    private static TargetProfile IieProfile(string name, string description, string cpu, bool mouseText) =>
        new(name, description + ", 64 KiB main RAM with banked language-card memory", cpu, 49152)
        {
            BankedMainMemoryBytes = 16384,
            AuxiliaryMemoryBytes = 65536,
            AuxiliaryMemoryRequirement = name == "apple2c" ? "Built-in 64 KiB auxiliary RAM" : "Requires a 64 KiB auxiliary memory expansion",
            Supports80ColumnText = true,
            SupportsMouseText = mouseText,
            MemoryBanks = [new("main", 0, 0xc000), new("aux", 0, 0xc000),
                new("lc1", 0xd000, 0x3000, "lc2"), new("lc2", 0xd000, 0x3000, "lc1"),
                new("aux-lc1", 0xd000, 0x3000, "aux-lc2"), new("aux-lc2", 0xd000, 0x3000, "aux-lc1")]
        };
}
