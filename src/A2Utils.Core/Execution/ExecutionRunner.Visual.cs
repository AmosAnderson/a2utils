// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Globalization;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Execution;

public static partial class ExecutionRunner
{
    private static ExecutionScreenshotResult? EvaluateScreenshot(ExecutionVisualInput? input, string artifacts,
        List<ProgramDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        if (input is null) return null;
        ExecutionScreenshotResult result = ExecutionVisual.Compare(input, artifacts, cancellationToken);
        if (!result.Comparison.Passed)
            diagnostics.Add(new("execution.screenshot_assertion", "error",
                $"Screenshot differs in {result.Comparison.DifferentPixels} of {result.Comparison.ComparedPixels} compared pixels.",
                File: result.DifferenceImage,
                Expected: "different fraction <= " + input.Options.MaxDifferentFraction.ToString("R", CultureInfo.InvariantCulture),
                Actual: result.Comparison.DifferentFraction.ToString("R", CultureInfo.InvariantCulture)));
        return result;
    }
}
