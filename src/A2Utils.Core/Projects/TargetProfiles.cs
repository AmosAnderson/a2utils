using A2Utils.Core.Assembly;

namespace A2Utils.Core.Projects;

public sealed record PlatformSymbol(string Name, int Address, string Description);
public sealed record TargetProfile(string Name, string Description, string Cpu, int MainMemoryBytes);

public static class TargetProfiles
{
    public static IReadOnlyList<TargetProfile> All { get; } = [
        new("apple2plus", "Apple II Plus, 48 KiB main RAM, Applesoft ROM", "6502", 49152),
        new("apple2e", "Unenhanced Apple IIe, main-memory development", "6502", 49152),
        new("apple2enh", "Enhanced Apple IIe, main-memory development", "65c02", 49152),
        new("apple2c", "Apple IIc, main-memory development", "65c02", 49152)
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
        new(runtime == "dos33" ? "DOS workspace and ROM/I/O" : "ProDOS MLI and ROM/I/O",
            runtime == "dos33" ? 0x9600 : 0xbf00, runtime == "dos33" ? 0x6a00 : 0x4100)
    ];
}
