# Third-party components

The root `LICENSE` applies to original A2Utils code only. The components below
retain their stated licenses and notices.

- CiderPress II DiskArc and CommonUtil: pinned revision
  `7a055a200e31f752f3a92bb9fe6ae6f67cd55534` from
  <https://github.com/fadden/CiderPress2>. Retained code license and attribution
  files are in `third_party/CiderPress2`. Source files are unchanged and
  enumerated in `SOURCE_MANIFEST.json`.
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
  contributors; the shared license is retained in
  `notices/Microsoft.Extensions/LICENSE.txt`.
- xUnit, its Visual Studio adapter, and Microsoft.NET.Test.Sdk are development
  dependencies; they are excluded from the tool distribution.
- Self-contained downloads include the .NET runtime and its third-party
  components; retain their license/notice files when redistributing.

Repository source/package lockfiles record exact dependency versions; the
runtime licenses above are also copied into packaged and published output. The
original synthetic fixtures contain no Apple operating-system or boot code.
