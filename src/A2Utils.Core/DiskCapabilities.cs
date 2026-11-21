// SPDX-FileCopyrightText: 2026 Amos Anderson
// SPDX-License-Identifier: MIT

namespace A2Utils.Core;

public sealed record DiskOperationCapability(string Operation, bool Supported, IReadOnlyList<string> Reasons);

/// <summary>Format and structural eligibility, independent of the current session's open mode.
/// Individual entry permissions, available space, and host destination permissions still apply.</summary>
public sealed record DiskCapabilities(IReadOnlyList<DiskOperationCapability> Operations,
    IReadOnlyList<string> MetadataFields)
{
    internal static DiskCapabilities Describe(DiskInfo info, bool embedded = false)
    {
        bool known = info.FileSystem is "dos33" or "prodos";
        List<string> writeReasons = [];
        if (!known) writeReasons.Add("unrecognized_filesystem");
        if (info.ImageWriteProtected) writeReasons.Add("image_read_only");
        if (info.IsDubious || info.Diagnostics.Any(item => item.Severity == "error"))
            writeReasons.Add("corrupt_image");
        if (embedded || info.Diagnostics.Any(item => item.Code == "hybrid_filesystem"))
            writeReasons.Add("hybrid_filesystem");
        if (info.Diagnostics.Any(item => item.Code is "unsupported_forks" or "damaged_entry"))
            writeReasons.Add("unsafe_allocation");
        string[] readReasons = known ? [] : ["unrecognized_filesystem"];
        string[] extractionReasons = readReasons.Concat(info.Diagnostics
            .Where(item => item.Code == "damaged_entry")
            .Select(item => item.Code)).Distinct(StringComparer.Ordinal).ToArray();
        string[] directoryReasons = writeReasons.Concat(info.FileSystem == "dos33"
            ? new[] { "unsupported_directories" } : []).ToArray();
        static DiskOperationCapability Operation(string name, IReadOnlyList<string> reasons)
            => new(name, reasons.Count == 0, reasons);
        return new(
        [
            Operation("info", []),
            Operation("list", readReasons),
            Operation("verify", readReasons),
            Operation("readFile", readReasons),
            Operation("extractAll", extractionReasons),
            Operation("edit", writeReasons),
            Operation("createDirectories", directoryReasons),
            Operation("updateAttributes", writeReasons),
            Operation("inspect", info.Order == "unknown" ? ["unknown_order"] : []),
            Operation("convert", info.Order == "unknown" ? ["unknown_order"] : []),
            Operation("resourceForks", info.FileSystem == "prodos" ? [] : ["unsupported_forks"])
        ],
            info.FileSystem switch
            {
                "dos33" => ["fileType", "locked", "binaryLoadAddress"],
                "prodos" => ["fileType", "auxType", "access", "created", "modified"],
                _ => []
            });
    }
}
