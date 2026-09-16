using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public sealed record Cc65Options
{
    public string Compiler { get; init; } = "cl65";
    public string Target { get; init; } = "apple2";
    public string? ExpectedVersion { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
    public bool Optimize { get; init; } = true;
    public List<string> AdditionalSources { get; init; } = [];
    public List<string> Includes { get; init; } = [];
    public List<string> Defines { get; init; } = [];
    public string? LinkerConfig { get; init; }
    public string? ToolchainRoot { get; init; }
    public Dictionary<string, string> SegmentBanks { get; init; } = new(StringComparer.Ordinal);
}

public sealed record Cc65Result(byte[] AppleSingle, string Version, string Map, string Labels,
    IReadOnlyList<BuildInput> Inputs)
{
    public string CompilerPath { get; init; } = "";
    public IReadOnlyList<BuildInput> ToolchainInputs { get; init; } = [];
    public IReadOnlyDictionary<string, int> Symbols { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<Cc65Segment> Segments { get; init; } = [];
    public IReadOnlyList<ProgramDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Runs optional cl65 against a bounded isolated copy of project source files.</summary>
public static class Cc65Compiler
{
    private const int MaximumInputCount = 4096;
    private const int MaximumInputBytes = 64 * 1024 * 1024;
    private const int MaximumCaptureCharacters = 1024 * 1024;
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".h", ".s", ".asm", ".a65", ".inc", ".bin"
    };

    public static Cc65Result Compile(string source, Cc65Options options, string projectRoot,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, byte[]>? generatedInputs = null)
        => CompileAsync(source, options, projectRoot, cancellationToken, generatedInputs).GetAwaiter().GetResult();

    private static async Task<Cc65Result> CompileAsync(string source, Cc65Options options, string projectRoot,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, byte[]>? generatedInputs)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Target is not ("apple2" or "apple2enh")) throw Error("target", "cc65 target must be apple2 or apple2enh.");
        if (options.TimeoutSeconds is < 1 or > 3600) throw Error("timeout", "Compiler timeout must be 1..3600 seconds.");
        if (options.AdditionalSources is null || options.Includes is null || options.Defines is null || options.SegmentBanks is null)
            throw Error("options", "Compiler source, include, and define lists cannot be null.");
        if (options.AdditionalSources.Count > 128 || options.Includes.Count > 128 || options.Defines.Count > 128)
            throw Error("options", "Compiler option lists are limited to 128 entries each.");
        if (options.SegmentBanks.Count > 4096 || options.SegmentBanks.Any(pair => string.IsNullOrWhiteSpace(pair.Key) ||
            pair.Value is not ("main" or "aux" or "lc1" or "lc2" or "aux-lc1" or "aux-lc2")))
            throw Error("segment_bank", "Segment bank declarations require named segments and physical Apple II memory banks.");
        foreach (string define in options.Defines)
        {
            if (define is null || define.Length > 1024 || !Regex.IsMatch(define, @"^[A-Za-z_][A-Za-z0-9_]*(?:=[^\r\n\0]*)?$", RegexOptions.CultureInvariant))
                throw Error("define", "Defines must use NAME or NAME=value without newlines.");
        }
        string root = Path.GetFullPath(projectRoot);
        ImageTransactions.ValidatePath(root);
        string main = RequireProjectPath(source, root);
        List<string> sources = [main];
        sources.AddRange(options.AdditionalSources.Select(path => RequireProjectPath(path, root)));
        if (sources.Distinct(PathComparer).Count() != sources.Count) throw Error("sources", "Source files must be distinct.");
        foreach (string path in sources)
        {
            if (Path.GetExtension(path).ToLowerInvariant() is not (".c" or ".s" or ".asm" or ".a65"))
                throw Error("source_type", "cc65 source must be a .c, .s, .asm, or .a65 file.");
            if (!File.Exists(path)) throw new FileNotFoundException("Compiler source does not exist.", path);
        }
        List<string> includes = options.Includes.Select(path => RequireProjectPath(path, root, allowRoot: true)).ToList();
        foreach (string include in includes)
            if (!Directory.Exists(include) && !(generatedInputs?.Keys.Any(path =>
            {
                string relative = Path.GetRelativePath(include, Path.GetFullPath(path, root));
                return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            }) ?? false))
                throw Error("include", $"Include directory does not exist and contains no generated inputs: {include}");
        string executable = ResolveCompiler(options.Compiler, root);
        string? linkerConfig = options.LinkerConfig is null ? null : RequireProjectPath(options.LinkerConfig, root);
        if (linkerConfig is not null && !Path.GetExtension(linkerConfig).Equals(".cfg", StringComparison.OrdinalIgnoreCase))
            throw Error("linker_config", "Custom linker configurations must use a .cfg extension.");
        byte[]? configBytes = linkerConfig is null ? null : ProgramFiles.ReadBytes(linkerConfig, 1024 * 1024, cancellationToken);
        IReadOnlyDictionary<string, string>? segmentKinds = configBytes is null ? null : Cc65LinkerConfiguration.Validate(ProgramFiles.DecodeText(configBytes));
        string temporaryRoot = ResolvePhysicalDirectory(Path.GetTempPath());
        string temporary = Path.Combine(temporaryRoot, $"a2-cc65-{Guid.NewGuid():N}");
        ImageTransactions.ValidatePath(temporary);
        Directory.CreateDirectory(temporary);
        try
        {
            string stage = Path.Combine(temporary, "src");
            Directory.CreateDirectory(stage);
            List<BuildInput> inputs = StageProject(root, stage, sources, cancellationToken);
            IReadOnlyList<BuildInput> toolchainInputs = FingerprintToolchain(executable, options.ToolchainRoot, root, cancellationToken);
            inputs.AddRange(toolchainInputs);
            long generatedBytes = 0;
            if (generatedInputs is not null && generatedInputs.Count > MaximumInputCount)
                throw Error("generated_input", "Generated compiler input count exceeds 4096.");
            if (generatedInputs is not null)
                foreach (var generated in generatedInputs)
                {
                    string original = RequireProjectPath(generated.Key, root);
                    if (File.Exists(original)) throw Error("generated_collision", "Generated compiler input conflicts with an existing project file.");
                    string relative = Path.GetRelativePath(root, original);
                    string staged = Path.Combine(stage, relative);
                    if (!SourceExtensions.Contains(Path.GetExtension(original)) || generated.Value.Length > MaximumInputBytes)
                        throw Error("generated_input", "Generated compiler inputs must be bounded supported source files.");
                    generatedBytes += generated.Value.Length;
                    if (generatedBytes > MaximumInputBytes) throw Error("generated_input", "Generated compiler inputs exceed 64 MiB.");
                    if (!Path.GetExtension(original).Equals(".bin", StringComparison.OrdinalIgnoreCase))
                        ValidateIncludes(ProgramFiles.DecodeText(generated.Value), original, root);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, generated.Value);
                    inputs.Add(new(relative.Replace('\\', '/'), ProgramFiles.Hash(generated.Value)));
                }
            if (linkerConfig is not null)
            {
                string relative = Path.GetRelativePath(root, linkerConfig);
                string staged = Path.Combine(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                File.WriteAllBytes(staged, configBytes!);
                inputs.Add(new(relative.Replace('\\', '/'), ProgramFiles.Hash(configBytes!)));
            }
            string? toolchainRoot = options.ToolchainRoot is null ? null : Path.GetFullPath(options.ToolchainRoot, root);
            ProcessResult versionResult = await Run(executable, ["--version"], temporary, options.TimeoutSeconds, cancellationToken, toolchainRoot);
            if (versionResult.ExitCode != 0) throw Error("version", "cl65 --version failed: " + versionResult.Details);
            string version = versionResult.Details.Trim();
            if (string.IsNullOrWhiteSpace(version)) throw Error("version", "cl65 did not report a version.");
            if (options.ExpectedVersion is not null && !version.Equals(options.ExpectedVersion, StringComparison.Ordinal))
                throw Error("version_mismatch", $"Compiler reported '{version}'; expected '{options.ExpectedVersion}'.");
            string binary = Path.Combine(temporary, "program.as");
            string map = Path.Combine(temporary, "program.map");
            string labels = Path.Combine(temporary, "program.lbl");
            List<string> arguments = ["-t", options.Target, "-g", "-o", binary, "-m", map, "-Ln", labels];
            if (linkerConfig is not null) arguments.AddRange(["-C", Path.Combine(stage, Path.GetRelativePath(root, linkerConfig))]);
            if (options.Optimize) arguments.Add("-O");
            foreach (string include in includes)
            {
                string stagedInclude = Path.Combine(stage, Path.GetRelativePath(root, include));
                arguments.AddRange(["-I", stagedInclude, "--asm-include-dir", stagedInclude, "--bin-include-dir", stagedInclude]);
            }
            foreach (string define in options.Defines) arguments.AddRange(["-D", define]);
            arguments.AddRange(sources.Select(path => Path.Combine(stage, Path.GetRelativePath(root, path))));
            ProcessResult compiled = await Run(executable, arguments, stage, options.TimeoutSeconds, cancellationToken, toolchainRoot);
            List<ProgramDiagnostic> diagnostics = Cc65Feedback.ParseDiagnostics(compiled.Details, stage, root, compiled.ExitCode != 0).ToList();
            if (compiled.ExitCode != 0)
            {
                string details = compiled.Details.Replace(stage, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                throw new DiskException("cc65.compile_failed", details.Length == 0 ? $"cl65 exited {compiled.ExitCode}." : details, 2)
                {
                    Diagnostics = diagnostics.Count == 0 ? [new("cc65.compile_failed", "error", details, main)] : diagnostics
                };
            }
            byte[] appleSingle = ProgramFiles.ReadBytes(binary, 128 * 1024, cancellationToken);
            AppleSingleProgram.Decode(appleSingle);
            string mapText = ProgramFiles.ReadText(map, cancellationToken);
            string labelText = ProgramFiles.ReadText(labels, cancellationToken);
            IReadOnlyList<Cc65Segment> segments = Cc65Feedback.ParseSegments(mapText, segmentKinds);
            if (segments.Count == 0) diagnostics.Add(new("cc65.memory_incomplete", "warning", "The linker map contains no recognized segments; declare runtimeMemory for allocations not covered by the payload."));
            if (options.ToolchainRoot is null) diagnostics.Add(new("cc65.toolchain_partial", "warning", "Only the compiler executable is fingerprinted; set toolchainRoot to include installed tools, libraries, headers and configurations."));
            foreach (BuildInput input in toolchainInputs)
                if (ProgramFiles.Hash(ProgramFiles.ReadBytes(input.Path, MaximumInputBytes, cancellationToken)) != input.Sha256)
                    throw Error("toolchain_changed", "The compiler distribution changed during compilation.");
            mapText = Regex.Replace(mapText, @"(\.(?:c|s|asm|a65))\.\d+\.\d+(\.o\b)", "$1$2", RegexOptions.CultureInvariant);
            return new(appleSingle, version, mapText.Replace(stage, ".", StringComparison.Ordinal),
                labelText.Replace(stage, ".", StringComparison.Ordinal), inputs.OrderBy(i => i.Path, StringComparer.Ordinal).ToArray())
            {
                CompilerPath = executable,
                ToolchainInputs = toolchainInputs,
                Symbols = Cc65Feedback.ParseLabels(labelText),
                Segments = segments,
                Diagnostics = diagnostics
            };
        }
        finally
        {
            // This directory is generated here, never obtained from project configuration.
            await CleanupGeneratedDirectoryAsync(temporary);
        }
    }

    private static IReadOnlyList<BuildInput> FingerprintToolchain(string executable, string? configuredRoot, string projectRoot,
        CancellationToken cancellationToken)
    {
        Dictionary<string, BuildInput> inputs = new(PathComparer);
        long total = 0;
        Add(executable);
        if (configuredRoot is not null)
        {
            string root = Path.GetFullPath(configuredRoot, projectRoot);
            ImageTransactions.ValidatePath(root);
            if (!Directory.Exists(root)) throw Error("toolchain_root", "Configured toolchainRoot does not exist.");
            foreach (string name in new[] { "bin", "include", "asminc", "lib", "cfg" })
            {
                string directory = Path.Combine(root, name);
                if (!Directory.Exists(directory)) continue;
                Stack<string> pending = new();
                pending.Push(directory);
                int directories = 0;
                while (pending.TryPop(out string? current))
                {
                    ImageTransactions.ValidatePath(current);
                    if (++directories > MaximumInputCount) throw Error("toolchain_limit", "Toolchain contains too many directories.");
                    foreach (string path in Directory.EnumerateFiles(current).Order(StringComparer.Ordinal)) Add(path);
                    foreach (string child in Directory.EnumerateDirectories(current).Order(StringComparer.Ordinal)) pending.Push(child);
                }
            }
        }
        return inputs.Values.OrderBy(input => input.Path, StringComparer.Ordinal).ToArray();

        void Add(string path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (inputs.ContainsKey(path)) return;
            if (inputs.Count >= MaximumInputCount) throw Error("toolchain_limit", "Toolchain exceeds 4096 files.");
            byte[] bytes = ProgramFiles.ReadBytes(path, MaximumInputBytes, cancellationToken);
            total += bytes.Length;
            if (total > 256L * 1024 * 1024) throw Error("toolchain_limit", "Toolchain snapshot exceeds 256 MiB.");
            inputs[path] = new(path, ProgramFiles.Hash(bytes));
        }
    }

    internal static string ResolvePhysicalDirectory(string path)
        => ResolvePhysicalDirectory(path, new(PathComparer));

    private static string ResolvePhysicalDirectory(string path, HashSet<string> visited)
    {
        string fullPath = Path.GetFullPath(path);
        if (!visited.Add(fullPath)) throw new IOException("A directory-link cycle was found while resolving the temporary path.");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("The temporary directory does not exist.");

        string root = Path.GetPathRoot(fullPath)!;
        string resolved = root;
        foreach (string segment in fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            DirectoryInfo directory = new(Path.Combine(resolved, segment));
            FileSystemInfo? target = directory.ResolveLinkTarget(returnFinalTarget: true);
            resolved = target is null ? directory.FullName : ResolvePhysicalDirectory(target.FullName, visited);
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    internal static async Task CleanupGeneratedDirectoryAsync(string path)
    {
        const int attempts = 10;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A terminated compiler descendant can briefly retain its working directory,
                // especially on Windows. This unique internal directory is safe to abandon if
                // bounded retries cannot remove it; cleanup must not mask the compile outcome.
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }

    private static List<BuildInput> StageProject(string root, string destination, List<string> sources,
        CancellationToken cancellationToken)
    {
        List<BuildInput> inputs = [];
        Stack<string> directories = new();
        directories.Push(root);
        int total = 0;
        int directoryCount = 0;
        long totalBytes = 0;
        while (directories.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++directoryCount > MaximumInputCount) throw Error("input_limit", $"Project exceeds {MaximumInputCount} source directories.");
            ImageTransactions.ValidatePath(directory);
            foreach (string child in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "artifacts" or "bin" or "obj" or "node_modules" or "third_party") continue;
                ImageTransactions.ValidatePath(child);
                directories.Push(child);
            }
            foreach (string path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SourceExtensions.Contains(Path.GetExtension(path))) continue;
                if (++total > MaximumInputCount) throw Error("input_limit", $"Project exceeds {MaximumInputCount} compiler input files.");
                byte[] bytes = ProgramFiles.ReadBytes(path, cancellationToken: cancellationToken);
                totalBytes += bytes.Length;
                if (totalBytes > MaximumInputBytes) throw Error("input_limit", $"Compiler project exceeds {MaximumInputBytes} source bytes.");
                string relative = Path.GetRelativePath(root, path);
                if (!Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)) ValidateIncludes(ProgramFiles.DecodeText(bytes), path, root);
                string output = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, bytes);
                inputs.Add(new(relative.Replace('\\', '/'), ProgramFiles.Hash(bytes)));
            }
        }
        foreach (string source in sources)
            if (!File.Exists(Path.Combine(destination, Path.GetRelativePath(root, source))))
                throw Error("source_path", "Sources under excluded build or hidden directories are unsupported.");
        return inputs;
    }

    private static void ValidateIncludes(string source, string path, string root)
    {
        bool isC = Path.GetExtension(path).Equals(".c", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".h", StringComparison.OrdinalIgnoreCase);
        // C removes continued newlines before comments, so validate the same logical directives
        // seen by its preprocessor. A regex over physical source misses #inc\\\nlude and #include/**/.
        if (isC)
        {
            string cSource = NormalizeIncludeSource(source, assembly: false);
            foreach (Match match in Regex.Matches(cSource, @"(?im)^[ \t\v\f]*#[ \t\v\f]*include\b([^\r\n]*)", RegexOptions.CultureInvariant))
                ValidateOperand(match.Groups[1].Value, assembly: false, binary: false);
            return;
        }

        // ca65 allows labels before directives and does not require whitespace before a quoted
        // argument. Scan outside strings/comments rather than anchoring directives at line start.
        string assemblySource = NormalizeIncludeSource(source, assembly: true);
        for (int index = 0; index < assemblySource.Length; index++)
        {
            if (assemblySource[index] is '\'' or '"')
            {
                char quote = assemblySource[index++];
                while (index < assemblySource.Length && assemblySource[index] != quote)
                {
                    index++;
                }
                continue;
            }
            if (assemblySource[index] != '.' || index > 0 && (char.IsAsciiLetterOrDigit(assemblySource[index - 1]) || assemblySource[index - 1] == '_')) continue;
            int end = index + 1;
            while (end < assemblySource.Length && (char.IsAsciiLetterOrDigit(assemblySource[end]) || assemblySource[end] == '_')) end++;
            string directive = assemblySource[(index + 1)..end];
            int lineEnd = assemblySource.IndexOfAny(['\r', '\n'], end);
            if (lineEnd < 0) lineEnd = assemblySource.Length;
            if (directive.Equals("feature", StringComparison.OrdinalIgnoreCase) || directive.Equals("linecont", StringComparison.OrdinalIgnoreCase))
                throw Error("include_syntax", "ca65 compatibility switches are unsupported in the isolated compiler adapter; use the default ca65 source syntax.");
            if (!directive.Equals("include", StringComparison.OrdinalIgnoreCase) && !directive.Equals("incbin", StringComparison.OrdinalIgnoreCase)) continue;
            ValidateOperand(assemblySource[end..lineEnd], assembly: true, binary: directive.Equals("incbin", StringComparison.OrdinalIgnoreCase));
            index = lineEnd;
        }

        void ValidateOperand(string operand, bool assembly, bool binary)
        {
            string argument = operand.Trim();
            bool system = !assembly && argument.StartsWith('<');
            if (!system && !argument.StartsWith('"'))
                throw Error("include_macro", $"Macro-based includes and computed include filenames are unsupported: {path}");
            int closing = argument.IndexOf(system ? '>' : '"', 1);
            if (closing < 0) throw Error("include", $"Unclosed include path: {path}");
            string include = argument[1..closing];
            if (assembly && include.Contains('\\'))
                throw Error("include_path", "Use forward slashes and literal characters in ca65 include filenames; escaped filenames are unsupported.");
            string trailing = argument[(closing + 1)..].Trim();
            if (trailing.Length > 0 && !(binary && trailing.StartsWith(',')))
                throw Error("include_macro", $"Only literal include filenames are supported: {path}");
            // Treat both separator spellings conservatively on every host.
            string portable = include.Replace('\\', '/');
            if (Path.IsPathRooted(portable) || Regex.IsMatch(portable, @"^[A-Za-z]:", RegexOptions.CultureInvariant))
                throw Error("include_path", "Absolute project include paths are unsupported.");
            if (!binary)
            {
                string extension = Path.GetExtension(portable).ToLowerInvariant();
                bool supported = assembly ? extension is ".s" or ".asm" or ".a65" or ".inc" : extension is ".c" or ".h";
                if (!supported) throw Error("include_type", "C text includes must use .c/.h; ca65 text includes must use .s/.asm/.a65/.inc. Use .incbin for binary data.");
            }
            if (system) RequireProjectPath(portable, root);
            else RequireProjectPath(Path.Combine(Path.GetDirectoryName(path)!, portable), root);
        }
    }

    private static string NormalizeIncludeSource(string source, bool assembly)
    {
        // ca65 defaults differ from C: only semicolons start comments, strings use literal
        // backslashes, and continued lines are disabled. Never splice an assembly comment.
        string spliced = assembly ? source : source.Replace("\\\r\n", "", StringComparison.Ordinal)
            .Replace("\\\n", "", StringComparison.Ordinal).Replace("\\\r", "", StringComparison.Ordinal);
        StringBuilder normalized = new(spliced.Length);
        for (int index = 0; index < spliced.Length; index++)
        {
            char current = spliced[index];
            if (current is '\'' or '"')
            {
                char quote = current;
                normalized.Append(current);
                while (++index < spliced.Length)
                {
                    if (assembly && spliced[index] is '\r' or '\n')
                        throw Error("include_syntax", "Unclosed ca65 string or character constant; default ca65 source syntax is required.");
                    normalized.Append(spliced[index]);
                    if (!assembly && spliced[index] == '\\' && index + 1 < spliced.Length) normalized.Append(spliced[++index]);
                    else if (spliced[index] == quote) break;
                }
            }
            else if (assembly && current == ';' || !assembly && current == '/' && index + 1 < spliced.Length && spliced[index + 1] == '/')
            {
                while (index < spliced.Length && spliced[index] is not ('\r' or '\n')) index++;
                if (index < spliced.Length) normalized.Append(spliced[index]);
            }
            else if (!assembly && current == '/' && index + 1 < spliced.Length && spliced[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < spliced.Length && !(spliced[index] == '*' && spliced[index + 1] == '/')) index++;
                index++;
                normalized.Append(' ');
            }
            else if (assembly && current == '\\' && index + 1 < spliced.Length && spliced[index + 1] is '\r' or '\n')
                throw Error("include_syntax", "Continued ca65 source lines are unsupported; use default ca65 source syntax.");
            else normalized.Append(current);
        }
        return normalized.ToString();
    }

    private static string RequireProjectPath(string path, string root, bool allowRoot = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Error("path", "Compiler paths must not be empty.");
        string full = Path.GetFullPath(path, root);
        string relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || (!allowRoot && relative == "."))
            throw Error("path_escape", "Compiler project input paths must remain within the project root.");
        ImageTransactions.ValidatePath(full);
        return full;
    }

    private static string ResolveCompiler(string name, string root)
    {
        if (string.IsNullOrWhiteSpace(name)) throw Error("compiler", "A compiler executable is required.");
        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            string full = Path.GetFullPath(name, root);
            ImageTransactions.ValidatePath(full);
            if (File.Exists(full)) return full;
        }
        else
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string path = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name);
                if (!File.Exists(path)) continue;
                ImageTransactions.ValidatePath(path);
                return Path.GetFullPath(path);
            }
        }
        throw new DiskException("cc65.compiler_missing", $"Cannot find cl65 executable '{name}'. Install cc65 or set compiler to its path.", 5);
    }

    private static async Task<ProcessResult> Run(string executable, IEnumerable<string> arguments,
        string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken, string? toolchainRoot = null)
    {
        using Process process = new();
        process.StartInfo = new(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (toolchainRoot is not null)
        {
            process.StartInfo.Environment["CC65_HOME"] = toolchainRoot;
            foreach (string name in new[] { "CC65_INC", "CA65_INC", "LD65_LIB", "LD65_OBJ", "LD65_CFG" })
                process.StartInfo.Environment.Remove(name);
            process.StartInfo.Environment.TryGetValue("PATH", out string? inheritedPath);
            process.StartInfo.Environment["PATH"] = Path.Combine(toolchainRoot, "bin") + Path.PathSeparator + inheritedPath;
        }
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(timeoutSeconds));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            if (!process.Start()) throw Error("start", "The compiler process could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new DiskException("cc65.compiler_start", "Cannot start cl65: " + exception.Message, 5, exception);
        }
        Task<string> stdout = Capture(process.StandardOutput, linked.Token);
        Task<string> stderr = Capture(process.StandardError, linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            string output = await stdout;
            string error = await stderr;
            return new(process.ExitCode, string.Join("\n", new[] { output.Trim(), error.Trim() }.Where(s => s.Length > 0)));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw new DiskException("cc65.timeout", "Compiler exceeded its timeout and was terminated.", 6);
        }
    }

    private static async Task<string> Capture(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            int available = MaximumCaptureCharacters - text.Length;
            if (available > 0) text.Append(buffer, 0, Math.Min(count, available));
        }
        return text.ToString();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static DiskException Error(string code, string message) => new($"cc65.{code}", message, 2);
    private sealed record ProcessResult(int ExitCode, string Details);
}
