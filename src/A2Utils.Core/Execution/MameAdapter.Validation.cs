// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

namespace A2Utils.Core.Execution;

public static partial class MameAdapter
{
    private static void ValidateDevelopment(ExecutionSpec spec)
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new DiskException("execution.invalid_spec", message, 2);
        }
        bool iie = spec.Machine is "apple2e" or "apple2ee" or "apple2c";
        Require(spec.TextColumns is 40 or 80, "textColumns must be 40 or 80.");
        Require(iie || spec.TextColumns == 40 && !spec.DecodeIIeText,
            "IIe text decoding and 80 columns require an Apple IIe/IIc machine.");
        Require(spec.ObserveMemory is not null && spec.ObserveMemory.Count <= 1024,
            "observeMemory must be an array of at most 1024 ranges.");
        foreach (MemoryCapture range in spec.ObserveMemory!)
        {
            Require(range is not null && AppleIIeMemory.IsValidRange(range.Bank, range.Address, range.Length),
                "Observed memory ranges must fit the selected bank without I/O reads.");
        }
        IEnumerable<string> banks = spec.ObserveMemory.Select(r => r.Bank)
            .Concat(spec.Memory?.Where(r => r is not null).Select(r => r.Bank) ?? []);
        if (spec.Until is not null) banks = banks.Append(spec.Until.Bank);
        Require(iie || banks.All(bank => bank == "cpu"), "Physical bank observations require an Apple IIe/IIc machine.");
        Require(spec.ObserveMemory.Select(r => (r.Bank, r.Address)).Distinct().Count() == spec.ObserveMemory.Count,
            "Observed memory start addresses must be unique within a bank.");
        Require(!spec.ObserveMemory.Any(r => spec.Memory?.Any(a => a is not null && a.Bank == r.Bank && a.Address == r.Address) == true),
            "An observation and assertion cannot share the same bank and start address.");
        if (spec.Debug is not { } debug) return;
        Require(debug.Breakpoints is not null && debug.Watchpoints is not null &&
            debug.Breakpoints.Count + debug.Watchpoints.Count is > 0 and <= 64,
            "debug requires 1..64 breakpoints or watchpoints.");
        Require(debug.StepInstructions is >= 0 and <= 1024 && debug.HistoryInstructions is >= 0 and <= 256,
            "Debug steps must be 0..1024 and history instructions 0..256.");
        Require(spec.Until is null, "Choose a debug trigger or until completion condition, not both.");
        foreach (ExecutionBreakpoint point in debug.Breakpoints!)
        {
            Require(point is not null && point.Address is >= 0 and <= 65535 && point.Program is null && point.Symbol is null && point.Offset == 0,
                "Breakpoints require a numeric address; resolve program/symbol addresses using build --test.");
            Require(double.IsFinite(point!.AfterSeconds) && point.AfterSeconds >= 0 && point.AfterSeconds < spec.EmulatedSeconds,
                "Breakpoint afterSeconds must precede the emulated deadline.");
        }
        foreach (ExecutionWatchpoint point in debug.Watchpoints!)
        {
            Require(point is not null && point.Address is >= 0 and <= 65535 && point.Length is > 0 and <= 65536
                && (long)point.Address + point.Length <= 65536 && point.Access is "read" or "write" or "readWrite"
                && point.Program is null && point.Symbol is null && point.Offset == 0,
                "Watchpoints require a CPU range and access read/write/readWrite; use build --test for symbols.");
            Require(double.IsFinite(point!.AfterSeconds) && point.AfterSeconds >= 0 && point.AfterSeconds < spec.EmulatedSeconds,
                "Watchpoint afterSeconds must precede the emulated deadline.");
        }
    }
}
