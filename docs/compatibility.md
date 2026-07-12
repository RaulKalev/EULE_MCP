# Compatibility matrix

This document is the source of truth for build and Revit compatibility.

| Target | Runtime | Revit | Current C# | Intended surface |
|---|---|---|---:|---|
| `net48` | .NET Framework 4.8 | 2024 | 12.0 (transitional) | Full add-in and modeless WPF UI, matching `net8.0-windows` except IFC Space-to-Room (held back separately) |
| `net8.0-windows` | .NET 8 | 2026 | 12.0 | Full add-in and modeless WPF UI |
| `netstandard2.0` | Shared library | 2021+ compatible boundary | 10.0 (transitional) | Contracts and pure safety logic |

The build SDK is pinned to .NET SDK 9 by `global.json` because the repository's
`.slnx` format is not supported by .NET SDK 8. The produced Revit 2026 add-in
still targets .NET 8. Revit SDK package versions are pinned in
`RevitMCP.Addin.csproj`. Update any of these only through a reviewed
compatibility change that builds both add-in targets and runs the complete
unit-test suite.

## C# 7.3 migration

Repository policy requires code shared with Revit 2021-2024 to be compatible
with C# 7.3. The current legacy source predates that enforcement and uses
nullable reference syntax, records, target-typed construction, and collection
expressions. Its explicit C# 12 setting is therefore transitional, not the
target state.

Migrate by domain, keeping each change behavior-preserving:

1. Move .NET 8-only UI and implementation details behind the `REVIT2026`
   boundary.
2. Replace modern syntax in `RevitMCP.Core` and code compiled for `net48`.
3. Set `RevitMCP.Core` and the `net48` add-in target to C# 7.3.
4. Add a CI compile check that rejects newer syntax in those targets.

Do not add new post-C#-7.3 syntax to shared or `net48` code while this migration
is in progress. Newer syntax is allowed only in files compiled exclusively for
the Revit 2026 target.

## Revit API rules

- Default API usage must remain backportable to Revit 2021.
- APIs introduced after Revit 2021 must be isolated behind a compatibility
  adapter or an explicit version build constant.
- All model writes run on the Revit API thread inside a transaction.
- All WPF windows are modeless; confirmation flows must not call `ShowDialog()`.
