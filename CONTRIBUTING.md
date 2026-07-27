# Contributing to EF Core SQL Preview

Thanks for taking an interest. This document covers the project layout, how to build and test, and how to
debug the extension inside Visual Studio.

## Prerequisites

| | |
| --- | --- |
| .NET SDK | **10.0 or newer** (`dotnet --list-sdks`). Required to build the sample and to run the end-to-end tests. |
| Visual Studio | 2022 **17.14+** or 2026, with the **Visual Studio extension development** workload, if you want to debug the VSIX. |
| Git | Any recent version. |

Building the `Core` library and its tests needs only the SDK — no Visual Studio, no Windows-specific workload.
Only the VSIX project (`net8.0-windows8.0`) requires Windows.

`global.json` pins the SDK to `10.0.100` with `rollForward: latestFeature`, so any 10.0.x SDK works and an
11.x SDK will not silently be picked up.

## Project layout

```
EFCoreSqlPreview/
├─ src/EFCoreSqlPreview.Core/        netstandard2.0 + net8.0 class library. All the logic.
│  ├─ Analysis/                      Roslyn syntax analysis: selection resolution, terminal-operator and
│  │                                 projection classification, DbContext root, free variables, out-of-scope
│  │                                 detection. No semantic model, no MSBuildWorkspace.
│  ├─ Projects/                      ProjectLocator (find the .csproj) and ProviderDetector (find the EF
│  │                                 provider by reading MSBuild XML, without evaluating MSBuild).
│  ├─ Generation/                    WorkerTemplate (the fixed harness: capture interceptor, synthetic reader,
│  │                                 DbContext activation ladder) and WorkerCodeGenerator (fills it in).
│  ├─ Execution/                     PreviewRunner: analyse -> locate -> generate -> `dotnet run --file` ->
│  │                                 parse the JSON payload. Plus DiagnosticRemapper.
│  └─ Infrastructure/                IFileSystem and IProcessRunner, the two seams every test injects.
│
├─ EFCoreSqlPreview/                 The VSIX. net8.0-windows8.0, VisualStudio.Extensibility (out-of-proc).
│  ├─ Commands/                      The editor context-menu command and its VSCT group placement.
│  ├─ ToolWindows/                   Tool window, Remote UI XAML, view model, row models, formatting.
│  ├─ Services/                      Shared session state, captured selection, Win32 clipboard writer.
│  └─ Settings/                      PreviewSettings, persisted to %LOCALAPPDATA%\EFCoreSqlPreview.
│
├─ tests/EFCoreSqlPreview.Core.Tests/   xUnit + Shouldly. 814 tests.
│  ├─ Analysis/ Projects/ Generation/ Execution/    Fast unit tests (805, under a second).
│  └─ EndToEnd/                      9 tests that really run `dotnet run --file` against the sample.
│
└─ samples/SampleShop/               net10.0, EF Core 10. The fixture the end-to-end tests build against, and
                                     a good place to try the extension by hand.
```

**Layering rule:** `Core` must never reference anything from Visual Studio. It is `netstandard2.0` for exactly
that reason, and it must stay unit-testable without a VS host. The VSIX contains presentation logic only.

## Build

```powershell
dotnet build EFCoreSqlPreview.slnx                 # Debug
dotnet build EFCoreSqlPreview.slnx -c Release      # Release; also produces the .vsix
```

The installer lands at `EFCoreSqlPreview\bin\Release\net8.0-windows8.0\EFCoreSqlPreview.vsix`.

Both configurations must build with **0 warnings**. `TreatWarningsAsErrors` is deliberately off so a
contributor is never blocked mid-edit, but a PR that adds warnings will not be merged.

## Test

```powershell
# Everything (~27 s; the end-to-end tests shell out to dotnet run)
dotnet test EFCoreSqlPreview.slnx

# The fast loop (~0.7 s) - use this while iterating
dotnet test tests/EFCoreSqlPreview.Core.Tests --filter "Category!=EndToEnd"

# Only the end-to-end tests, with the captured SQL printed
dotnet test tests/EFCoreSqlPreview.Core.Tests --filter "Category=EndToEnd" --logger "console;verbosity=detailed"
```

The end-to-end tests **skip rather than fail** when no .NET 10 SDK is present; the skip reason lists the SDKs
that were found. They are serialized into one xUnit collection, because concurrent `dotnet run` invocations
fight over the same MSBuild locks.

### Writing tests

- `tests/.../Analysis/Fixture.cs` builds a document and analyses a marked span.
- `TestSource.Parse("var x = [|db.Products.ToList()|];")` returns the text plus the marked `TextSpan`;
  `TestSource.Caret("...To$$List()...")` gives a zero-length span for caret-only cases.
- `Generation/InMemoryFileSystem.cs` and `Execution/FakeProcessRunner.cs` let you exercise the whole pipeline
  without touching disk or spawning a process.
- `Projects/TempWorkspace.cs` writes throwaway `.csproj` trees under `%TEMP%\efcoresqlpreview-tests\`.

Anything that changes generated worker code, provider detection, or the analyzer's classification should come
with a test. Anything that changes the observable SQL should come with an end-to-end test.

## Debugging the extension

The VSIX is **out-of-process**: it runs in its own .NET process, talks to Visual Studio over RPC, and has no VS
main thread to marshal to. That makes it debuggable like an ordinary .NET app.

1. Open `EFCoreSqlPreview.slnx` in Visual Studio.
2. Set **EFCoreSqlPreview** as the startup project.
3. Press <kbd>F5</kbd>.

This launches the **Visual Studio experimental instance** with your build of the extension deployed into it.
The experimental instance is a separate hive, so it cannot disturb your normal Visual Studio settings.

4. In the experimental instance, open a solution that uses EF Core — `samples/SampleShop` is right there, and
   `samples/SampleShop/SampleQueries.cs` contains seven queries chosen to exercise different paths (custom
   extensions, free variables, `CountAsync`, `ToDictionaryAsync` with an `int[]`, `Include`/`ThenInclude`/
   `AsSplitQuery`, a grouped DTO, the query-builder shape, and query syntax with no terminal).
5. Select one and invoke **Preview EF Core SQL**.

Breakpoints in both `EFCoreSqlPreview` and `EFCoreSqlPreview.Core` are hit normally.

### Resetting the experimental instance

If it gets into a bad state:

```powershell
& "${env:ProgramFiles}\Microsoft Visual Studio\2022\<edition>\Common7\IDE\devenv.exe" /rootSuffix Exp /resetSettings
```

### Debugging the generated worker

Every run writes its program to `%LOCALAPPDATA%\EFCoreSqlPreview\scratch\<document>-<hash>\worker.cs`. It is a
self-contained .NET 10 file-based app — you can run it by hand:

```powershell
dotnet run --file "$env:LOCALAPPDATA\EFCoreSqlPreview\scratch\<document>-<hash>\worker.cs" --tl:off
```

It prints the JSON payload between `<<<EFSQLPREVIEW-BEGIN>>>` and `<<<EFSQLPREVIEW-END>>>`. This is the
fastest way to diagnose a generation bug: edit the file, re-run, and iterate without Visual Studio in the loop.

The directory name is a hash of the document path and selection; the file name varies by variant
(`worker.cs`, `worker-Sqlite.cs`, `worker-derived.cs`) so switching dialects keeps every variant warm. It is
always safe to delete the whole `scratch` directory.

### Remote UI gotcha

The tool window's XAML is loaded by Remote UI as an embedded resource named exactly
`EFCoreSqlPreview.ToolWindows.SqlPreviewControl.xaml`. Remote UI cannot host an `IValueConverter` or
code-behind, so every conditional visibility is a `Style` + `DataTrigger`, and failures are **silent** — a bad
binding produces a blank panel, not an exception. If a panel goes blank, that is where to look.

## Code style

- Nullable reference types enabled in `Core` and the VSIX.
- XML doc comments on every public type and member in `Core` (`GenerateDocumentationFile` is on there, and
  only there).
- File-scoped namespaces in `Core`, tests and the sample. The VSIX uses block namespaces — keep that
  consistent within that project.
- Comments explain **why**, not what. Several constants in `WorkerTemplate.cs` were established empirically
  and carry a comment saying so; do not "tidy" them.
- No emoji in source code.

## Pull requests

1. Fork and branch from `master`.
2. Make the change, with tests.
3. `dotnet build EFCoreSqlPreview.slnx -c Release` and `dotnet test EFCoreSqlPreview.slnx` must both be clean.
4. Update [CHANGELOG.md](CHANGELOG.md) under `## [Unreleased]`.
5. Open the PR, describing what changed and why. If it changes generated SQL, paste the before and after.

## Reporting bugs

Please use the [issue templates](https://github.com/kerols1234/EFCoreSqlPreview/issues/new/choose). For a
preview that produced the wrong result, turning on **Verbose** in the tool window and attaching the
**Diagnostics** and **Generated program** tabs makes the difference between a guess and a fix.
