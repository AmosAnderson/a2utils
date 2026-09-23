// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: GPL-2.0-only

using A2Utils.Core.Operations;
using A2Utils.Core.Programs;

namespace A2Utils.Core.Projects;

public static partial class ProjectBuilder
{
    private sealed record GeneratedAssetSet(IReadOnlyDictionary<string, byte[]> Outputs, IReadOnlyList<ProjectAssetReport> Reports);

    private static GeneratedAssetSet PrepareAssets(ProjectManifest manifest, string root, string output,
        Func<string, int, byte[]> readInput, CancellationToken cancellationToken)
    {
        if (manifest.Assets is null || manifest.Assets.Count > 128 || manifest.Assets.Any(asset => asset is null))
            throw Error("assets", "Assets must be an array of at most 128 conversion steps.");
        if (manifest.Assets.Select(asset => asset.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Assets.Count)
            throw Error("asset_name", "Asset names must be unique.");
        Dictionary<string, byte[]> generated = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        List<ProjectAssetReport> reports = [];
        long inputBytes = 0, outputBytes = 0;
        foreach (ProjectAsset asset in manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectAssets.ValidatePath(asset.Source);
            string source = Path.GetFullPath(asset.Source, root);
            byte[] bytes = readInput(source, 32 * 1024 * 1024);
            inputBytes += bytes.Length;
            if (inputBytes > 64 * 1024 * 1024) throw Error("asset_size", "Combined asset inputs exceed 64 MiB.");
            ProjectAssetCompilation compiled = ProjectAssets.Compile(asset, bytes);
            List<BuildInput> outputs = [];
            foreach (var pair in compiled.Outputs)
            {
                string path = Path.GetFullPath(pair.Key, root);
                ImageTransactions.ValidatePath(path);
                ImageTransactions.EnsureDistinctPaths(path, output);
                if (File.Exists(path) || Directory.Exists(path) || !generated.TryAdd(path, pair.Value))
                    throw Error("asset_collision", $"Generated asset collides with an existing or generated path: {pair.Key}");
                outputBytes += pair.Value.Length;
                if (outputBytes > 32 * 1024 * 1024) throw Error("asset_size", "Combined generated asset outputs exceed 32 MiB.");
                outputs.Add(new(pair.Key, ProgramFiles.Hash(pair.Value)));
            }
            reports.Add(new(asset.Name, asset.Kind, asset.Source, outputs, compiled.Metadata));
        }
        return new(generated, reports);
    }
}
