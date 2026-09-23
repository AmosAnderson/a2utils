// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public static partial class ExecutionRunner
{
    private static void EvaluateInstrumentation(ExecutionSpec spec, ExecutionObservation observation, List<ProgramDiagnostic> diagnostics)
    {
        bool requested = spec.Routine is not null || spec.Cycles is not null;
        if (!requested)
        {
            if (observation.Cycles is not null)
                diagnostics.Add(new("execution.cycles_unexpected", "error", "Unexpected cycle measurement evidence."));
            return;
        }
        if (observation.Cycles is not { } cycles)
        {
            diagnostics.Add(new("execution.cycles_timeout", "error", "The routine or measured code window did not finish before the deadline."));
            return;
        }
        if (spec.Engine == "cpu" && observation.StopReason == "cpu_fault") return;
        int? start = spec.Cycles?.Start.Address ?? spec.Routine?.EntryPoint
            ?? (spec.Routine?.EntrySymbol is null ? spec.Routine?.Origin : null);
        int end = spec.Cycles?.End.Address ?? RoutineHarness.ReturnAddress;
        double after = spec.Cycles?.Start.AfterSeconds ?? spec.Routine!.StartAfterSeconds;
        long maximum = spec.Cycles?.MaxCycles ?? spec.Routine!.MaxCycles;
        if (start.HasValue && cycles.StartAddress != start.Value || cycles.EndAddress != end
            || spec.Engine == "mame" && observation.EmulatedSeconds < after
            || cycles.Returned && observation.StopReason != (spec.Routine is null ? "cycle_complete" : "routine_return")
            || !cycles.Returned && observation.StopReason != "cycle_limit")
            diagnostics.Add(new("execution.cycles_evidence", "error", "Cycle measurement evidence disagrees with the requested interval."));
        if (!cycles.Returned || cycles.Cycles > maximum)
            diagnostics.Add(new("execution.cycle_budget", "error", "The code exceeded its CPU cycle budget or did not return.",
                Expected: maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Actual: cycles.Cycles.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
