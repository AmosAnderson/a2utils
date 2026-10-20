// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using System.Globalization;
using A2Utils.Core.Basic;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public static partial class ProjectBuilder
{
    private static PreparedFile PrepareStartup(ProjectManifest manifest, List<PreparedFile> files)
    {
        ProjectStartup startup = manifest.Startup!;
        string source;
        if (startup.Steps.Count == 0)
        {
            PreparedFile program = FindProgram(startup.Program);
            string command = TypesEqual(program.Info.Type, "BAS") ? "RUN" : TypesEqual(program.Info.Type, "BIN") ? "BRUN"
                : throw Error("startup_type", "A BASIC launcher can RUN BASIC or BRUN BIN files.");
            ValidateBasicOrigin(program);
            if (command == "BRUN" && program.Info.EntryPoint != program.Info.Origin)
                throw Error("startup_entry", "BRUN startup requires entry point equal to load origin; use startup.steps with a final run action for a distinct entry point.");
            source = $"10 PRINT CHR$(4);\"{command} {program.Info.Path}\"\n";
        }
        else
        {
            List<string> statements = [];
            List<PreparedFile> loads = [];
            Dictionary<string, PreparedFile> loaded = new(StringComparer.OrdinalIgnoreCase);
            PreparedFile? finalBasic = null;
            for (int index = 0; index < startup.Steps.Count; index++)
            {
                ProjectStartupStep step = startup.Steps[index];
                PreparedFile program = FindProgram(step.Program);
                if (step.Action == "run" && index != startup.Steps.Count - 1)
                    throw Error("startup_sequence", "A run action must be the final startup step.");
                if (step.Action == "run" && TypesEqual(program.Info.Type, "BAS"))
                {
                    ValidateBasicOrigin(program);
                    finalBasic = program;
                    statements.Add($"PRINT CHR$(4);\"RUN {program.Info.Path}\"");
                    continue;
                }
                if (!TypesEqual(program.Info.Type, "BIN") || program.Info.Origin is null || program.Info.Length == 0)
                    throw Error("startup_type", "load/call and binary run actions require nonempty, load-addressed BIN files.");
                if (step.Action is "load" or "run")
                {
                    foreach (string replaced in loaded.Where(pair => Overlaps(pair.Value.Info, program.Info)).Select(pair => pair.Key).ToArray())
                        loaded.Remove(replaced);
                    loaded[program.Info.Path] = program;
                    loads.Add(program);
                    statements.Add($"PRINT CHR$(4);\"BLOAD {program.Info.Path}\"");
                }
                if (step.Action is "call" or "run")
                {
                    if (!loaded.ContainsKey(program.Info.Path))
                        throw Error("startup_not_loaded", $"'{program.Info.Path}' must be loaded before it is called and must not have been overwritten by another load.");
                    statements.Add("CALL " + program.Info.EntryPoint!.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (finalBasic is null) statements.Add("END");
            // Keep Applesoft's downward-growing string heap below every loaded binary.
            // The explicit workspace below HIMEM also bounds the launcher's variables.
            int? himem = loads.Count == 0 ? null : loads.Min(file => file.Info.Origin!.Value);
            if (himem is { } limit) statements.Insert(0, "HIMEM:" + limit.ToString(CultureInfo.InvariantCulture));
            source = string.Concat(statements.Select((statement, index) =>
                ((index + 1) * 10).ToString(CultureInfo.InvariantCulture) + " " + statement + "\n"));
            int launcherEnd = ApplesoftBasic.DefaultOrigin + ApplesoftBasic.Compile(source).Length + startup.WorkspaceBytes;
            int finalEnd = finalBasic is null ? 0 : ApplesoftBasic.DefaultOrigin + finalBasic.Info.Length
                + Math.Max(startup.WorkspaceBytes, manifest.BasicWorkspaceBytes);
            if (himem is { } ceiling && Math.Max(launcherEnd, finalEnd) > ceiling)
                throw Error("startup_memory", "Startup loads overlap the BASIC launcher, final BASIC program, or reserved workspace. Move the binaries higher or reduce the declared workspace.");
        }

        byte[] bytes = ApplesoftBasic.Compile(source);
        ProjectFile item = new() { Path = startup.Path, Kind = "basic", Replace = startup.Replace, Resident = false };
        return new(item, bytes, new(item.Path, "startup", "BAS", ApplesoftBasic.DefaultOrigin,
            ApplesoftBasic.DefaultOrigin, ApplesoftBasic.DefaultOrigin, bytes.Length, ProgramFiles.Hash(bytes), false,
            new Dictionary<string, int>(), null));

        PreparedFile FindProgram(string path)
        {
            PreparedFile program = files.SingleOrDefault(file => file.Info.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                ?? throw Error("startup_program", $"Startup program '{path}' must identify a manifest file.");
            if (program.Info.MemoryBank != "main")
                throw Error("startup_bank", "Generated startup loads and calls main-memory payloads; banked payloads require an application loader.");
            if (program.Info.Path.Any(ch => ch is '"' or '\r' or '\n' or ',' or ':' || ch < 0x20 || ch > 0x7e))
                throw Error("startup_name", "Startup program name cannot be represented safely in a DOS command string.");
            return program;
        }

        static void ValidateBasicOrigin(PreparedFile program)
        {
            if (TypesEqual(program.Info.Type, "BAS") && program.Info.Origin != ApplesoftBasic.DefaultOrigin)
                throw Error("startup_origin", "RUN startup requires BASIC at the default origin.");
        }

        static bool Overlaps(BuiltFile first, BuiltFile second)
            => first.Origin < second.Origin + second.Length && second.Origin < first.Origin + first.Length;
    }
}
