# Third-party components

- CiderPress II DiskArc and CommonUtil: pinned revision
  `7a055a200e31f752f3a92bb9fe6ae6f67cd55534` from
  <https://github.com/fadden/CiderPress2>. Retained code license and attribution
  files are in `third_party/CiderPress2`. Source files are unchanged and
  enumerated in `SOURCE_MANIFEST.json`.
- System.CommandLine 2.0.11: <https://github.com/dotnet/command-line-api>, MIT
  license, retained in `notices/System.CommandLine/LICENSE.md`. It is a runtime dependency of the CLI.
- xUnit, its Visual Studio adapter, and Microsoft.NET.Test.Sdk are development
  dependencies; they are excluded from the tool distribution.
- Self-contained downloads include the .NET runtime and its third-party
  components; retain their license/notice files when redistributing.

Repository source/package lockfiles record exact dependency versions. The
original synthetic fixtures contain no Apple operating-system or boot code.
