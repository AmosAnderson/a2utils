# Third-party components

The [MIT License](LICENSE) covers original A2Utils code, documentation, scripts,
examples, and synthetic fixtures. The components below retain their stated
licenses and notices.

- CiderPress II DiskArc and CommonUtil: pinned revision
  `7a055a200e31f752f3a92bb9fe6ae6f67cd55534` from
  <https://github.com/fadden/CiderPress2>. Upstream applies Apache-2.0 to its
  own code, subject to the exceptions in
  [LegalStuff.txt](third_party/CiderPress2/LegalStuff.txt). A2Utils patched
  `DiskArc/FS/ProDOS_FileEntry.cs` to synchronize ProDOS directory metadata.
  The patch is marked in the source and documented in
  [the vendor notes](third_party/CiderPress2/README.a2utils.md).
- The packaged DiskArc and CommonUtil assemblies contain eight boot-code arrays
  from Apple DOS 3.2.1 and 3.3, GS/OS, Apple Pascal, and Macintosh disks.
  Formatting a new ProDOS data disk also writes the GS/OS boot block to block
  zero. The repository does not document redistribution terms covering all
  eight arrays. Public binary publication is on hold pending removal or
  confirmation of those terms. See the vendored
  [DOS images](third_party/CiderPress2/DiskArc/FS/DOS_OSImage.cs),
  [ProDOS formatter](third_party/CiderPress2/DiskArc/FS/ProDOS.cs),
  [Pascal formatter](third_party/CiderPress2/DiskArc/FS/Pascal.cs), and
  [Macintosh blocks](third_party/CiderPress2/CommonUtil/MacBoot.cs).
- System.CommandLine 2.0.11: <https://github.com/dotnet/command-line-api>, MIT
  license, retained in `notices/System.CommandLine/LICENSE.md`. It is a runtime dependency of the CLI.
- ModelContextProtocol 2.2.0: <https://github.com/modelcontextprotocol/csharp-sdk>,
  commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`, Apache-2.0 license retained
  in `notices/ModelContextProtocol/LICENSE`. It is a runtime dependency of the
  CLI and supplies the local stdio MCP server. ModelContextProtocol.Core 2.2.0
  is redistributed under the same license.
- Microsoft.Extensions.AI.Abstractions 10.8.3 and the following 10.0.10
  Microsoft.Extensions runtime dependencies are redistributed transitively:
  Caching.Abstractions, Configuration.Abstractions,
  DependencyInjection.Abstractions, Diagnostics.Abstractions,
  FileProviders.Abstractions, Hosting.Abstractions, Logging.Abstractions,
  Options, and Primitives. They are MIT licensed by the .NET Foundation and
  contributors. Their shared license and the 10.0.10 packages' third-party
  notices are included in `notices/Microsoft.Extensions/`.
- xUnit, its Visual Studio adapter, and Microsoft.NET.Test.Sdk are development
  dependencies; they are excluded from the tool distribution.
- Self-contained downloads include the .NET runtime and its third-party
  components; retain their license/notice files when redistributing.

Repository source/package lockfiles record exact dependency versions; the
runtime licenses above are also copied into packaged and published output. The
original synthetic fixtures contain no Apple operating-system or boot code.
