# Architecture

How EF Core SQL Preview turns an editor selection into SQL without a database and without starting the user's
application.

- [The core idea](#the-core-idea)
- [Layers](#layers)
- [Stage 1 — capture the selection](#stage-1--capture-the-selection)
- [Stage 2 — Roslyn syntax analysis](#stage-2--roslyn-syntax-analysis)
- [Stage 3 — locate the project and detect the provider](#stage-3--locate-the-project-and-detect-the-provider)
- [Stage 4 — generate the worker](#stage-4--generate-the-worker)
- [Stage 5 — run it and capture the commands](#stage-5--run-it-and-capture-the-commands)
- [Stage 6 — parse and render](#stage-6--parse-and-render)
- [A complete generated worker](#a-complete-generated-worker)
- [Design decisions and the evidence behind them](#design-decisions-and-the-evidence-behind-them)
- [Performance](#performance)

---

## The core idea

Every alternative approach to "what SQL does this query produce?" either re-implements EF Core's query
pipeline (fragile, always behind) or runs the application (slow, needs a database).

This tool does neither. It **runs the user's real LINQ expression, terminal operator and all, against a real
`DbContext` on a real provider** — and then intercepts the two things that would require a database:

1. **Opening the connection.** `ConnectionOpening` returns `InterceptionResult.Suppress()`, so no socket is
   ever opened and the connection string is never resolved to a host.
2. **Executing the command.** `ReaderExecuting` captures `command.CommandText` and `command.Parameters`, then
   returns `InterceptionResult<DbDataReader>.SuppressWithResult(syntheticReader)`.

Everything upstream of that — model building, expression translation, parameter extraction, SQL generation —
is EF Core doing its normal job. The SQL is not approximated; it is the SQL EF Core built.

The hard part is not the interception. It is getting the user's query, with its custom extension methods, its
DTO types and its captured variables, into a program that compiles. That is what the rest of this document is
about.

---

## Layers

| Layer | Project | Target | Depends on |
| --- | --- | --- | --- |
| Logic | `src/EFCoreSqlPreview.Core` | `netstandard2.0` + `net8.0` | Roslyn (`Microsoft.CodeAnalysis.CSharp`) only |
| Presentation | `EFCoreSqlPreview` | `net8.0-windows8.0` | `Microsoft.VisualStudio.Extensibility.Sdk`, Core |
| Execution | the generated worker | `net10.0` file-based app | the **user's** project, EF Core |

`Core` has **no Visual Studio dependency at all**. That is not tidiness — it is what makes 757 unit tests run
in 0.7 seconds without a VS host.

The VSIX is **out-of-process** (VisualStudio.Extensibility). It runs in its own .NET process and talks to VS
over RPC. There is no main thread to marshal to and no `JoinableTaskFactory`; spawning `dotnet run` and
waiting seconds for it cannot stall the IDE.

---

## Stage 1 — capture the selection

`Commands/PreviewSqlCommand.cs` copies three plain values out of the editor snapshot:

- the full document text, **including unsaved edits**;
- the document path;
- the selection start and length (or the caret offset, when the selection is empty).

The editor snapshot and every `TextRange` taken from it are RPC-backed and must not outlive the call, which is
why only plain values leave the method. Box selection and multi-caret both land here; the first non-empty
selection is the one the user means.

---

## Stage 2 — Roslyn syntax analysis

`LinqSelectionAnalyzer` parses the document with `CSharpSyntaxTree.ParseText` and does **syntax-only**
analysis. No semantic model, no `MSBuildWorkspace`, no project load.

That constraint is the whole reason the round trip is fast, and it is affordable because **the real C#
compiler does the semantic work later**, when the generated worker is compiled against the user's project.
The analyzer never has to know what `ActiveOnly()` means; it only has to reproduce the text.

The pipeline:

1. **`SelectionNormalizer`** clamps the span to the document and trims trailing whitespace and semicolons.
2. **`SelectionResolver`** finds the smallest node covering the selection, descends from a statement to the
   expression inside it, then ascends outward through *transparent* nodes — parentheses, `await`, casts,
   member accesses, invocation targets, query clauses — to the outermost expression of the chain. A caret in
   the middle of `db.Products.Where(…).ToListAsync()` therefore yields the whole chain.
3. **`QueryChainWalker`** walks the chain to its leftmost receiver, stopping at a member access on `this` or
   `base`, and produces the ordered list of calls.
4. **`TerminalOperatorClassifier`** looks the last call up in `TerminalOperatorCatalog` — a hand-maintained
   table of every LINQ and EF terminal, each with its result shape, whether it is async, whether it takes a
   predicate, and whether it throws against an empty reader. When there is no terminal, one is synthesized
   (`ToListAsync` in an async context, `ToList` otherwise) and flagged.
5. **`ProjectionClassifier`** finds the last `Select`/`SelectMany` and classifies it: named DTO type,
   anonymous type, tuple, single member (scalar), computed, grouping, or absent (entity).
6. **`ContextRootResolver`** identifies the head of the chain (`_context`, `db`, `this._db`, `_uow.Context`)
   and resolves its declared type from a field, property, parameter, primary-constructor parameter or local
   in the same file. It also produces the **normalised query text**, with the root rewritten to `__efspCtx` —
   necessary because there is no `this` in the generated program.
7. **`FreeVariableCollector`** finds identifiers used inside the query but declared outside it, and for each
   records the declared type, the initializer, and how reproducible it is.
8. **`PrologueSlicer`** handles the multi-statement case: given a block and the anchor statement, it keeps only
   the preceding statements that actually contribute (a liveness sweep over reads and writes), so the
   `var q = …; if (x) q = q.Where(…);` shape replays correctly and unrelated statements do not.
9. **`UsingCollector`** gathers regular, global, static and aliased using directives plus the containing
   namespace chain.
10. **`OutOfScopeDetector`** scans for `ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`, `FromSql*`,
    `ExecuteSql*`, entity mutation calls and third-party bulk operators, and marks them as hard or soft blocks.

The result is a `QueryAnalysisResult` — an immutable record carrying the normalised query text, the prologue,
the terminal, the projection, the context root, the free variables, the usings, the out-of-scope finding, and
a list of `EFSP####` diagnostics.

### Free variables and reproducibility

`InitializerReproducibility` classifies a variable's initializer into a `ValueSource`:

| `ValueSource` | Meaning | Emitted as |
| --- | --- | --- |
| `LiteralInitializer` | `100m`, `"widget"`, `true` | the literal, verbatim |
| `ConstructibleInitializer` | `new[] { 1, 2, 3 }`, `[1, 2, 3]`, `new List<int>()` | the expression, verbatim |
| `WellKnownStatic` | `DateTime.UtcNow`, `Guid.NewGuid()` | the expression, verbatim |
| `TransitiveLocal` | depends on another reproducible local | resolved through, up to depth 3 |
| `SynthesizedDefault` | anything else — a parameter, a DI field, a service call | `DefaultValueSynthesizer.For(type)` |
| `UserSupplied` | the user typed a value in the Variables panel | their expression |

`DefaultValueSynthesizer` produces something that compiles for the declared type: `0`, `0m`, `""`, `false`,
`null` for any nullable, `Array.Empty<T>()`, `new List<T>()`, `Enumerable.Empty<T>()`, `new Dictionary<K,V>()`,
`default!` when the type is unknown.

**Synthesized values change the SQL**, and the tool says so. An empty `Contains` collection collapses a
predicate to `WHERE 0 = 1`. Every synthesized variable sets `RequiresUserValue`, which is what lights up the
Variables panel.

---

## Stage 3 — locate the project and detect the provider

`ProjectLocator` walks up from the document to the nearest `.csproj`.

`ProviderDetector` reads MSBuild XML **without evaluating MSBuild** — an evaluation would need the full SDK
resolution machinery and would cost seconds. It:

1. reads the project's `PackageReference` items;
2. resolves `$(Property)` version references from the `Directory.Build.props` chain;
3. resolves central versions from `Directory.Packages.props` (`PackageVersion` items);
4. follows `ProjectReference` transitively, so a query in a class library finds the data project's provider;
5. falls back to scanning the nearest `.sln`/`.slnx` for a provider-bearing sibling project — the case where
   the query lives in a library the host project references, rather than the other way round.

The first provider found wins. The result is a `ProjectContext` with the provider, its package version, the EF
Core version, the target framework, and any extra project paths that must be referenced.

**The trade-off:** `ProjectReference` values containing MSBuild properties or globs are skipped, and a
multi-targeting project reports its first target framework. Evaluating MSBuild would fix both and cost far more
than it is worth for an interactive command.

---

## Stage 4 — generate the worker

`WorkerCodeGenerator` fills `WorkerTemplate` — a fixed ~700-line harness — with the analysed pieces, and writes
the result to:

```
%LOCALAPPDATA%\EFCoreSqlPreview\scratch\<document-stem>-<sha256(path|start|length)>\worker.cs
```

The file name varies by variant (`worker.cs`, `worker-Sqlite.cs`, `worker-derived.cs`) so switching dialects
keeps every variant's build artifacts warm. `IFileSystem.WriteAllText` skips writes when the content is
identical, preserving timestamps so MSBuild's up-to-date check keeps holding.

The generated file is a **.NET 10 file-based app**: a single `.cs` file with `#:project` directives that the
SDK turns into an implicit project. This is the feature that makes the whole design tractable — referencing
the user's project is one line, and the real compiler resolves everything.

`WorkerCodeGenerator` also builds a **`LineMap`**: generated line number → offset in the user's document. That
is how a `CS0029` on generated line 35 becomes an error at the right place in the user's file.

### `#:property` directives

```
#:property PublishAot=false
#:property Nullable=disable
#:property TreatWarningsAsErrors=false
#:property EnableNETAnalyzers=false
#:property AnalysisMode=None
#:property GenerateDocumentationFile=false
#:property NoWarn=$(NoWarn);CS0168;CS0219;CS1998;CS8600;CS8602;CS8603;CS8604;CS8618;CS8625;NU1608;NU1701;NU1903
```

`PublishAot=false` is **required**: EF Core refuses to build the model otherwise. The rest exist so the user's
own strictness settings cannot fail a program the user did not write.

---

## Stage 5 — run it and capture the commands

`PreviewRunner` spawns:

```
dotnet run --file <worker.cs> --tl:off
```

`--tl:off` disables the terminal logger, whose cursor-control escape sequences would otherwise interleave with
the JSON payload on stdout.

### The interceptor

Verbatim from the generated program:

```csharp
public sealed class CaptureInterceptor : DbConnectionInterceptor, IDbCommandInterceptor
{
    private readonly Probe _probe;

    public CaptureInterceptor(Probe probe) => _probe = probe;

    public override InterceptionResult ConnectionOpening(DbConnection c, ConnectionEventData e, InterceptionResult r) => InterceptionResult.Suppress();
    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection c, ConnectionEventData e, InterceptionResult r, CancellationToken ct = default) => new(InterceptionResult.Suppress());
    public override InterceptionResult ConnectionClosing(DbConnection c, ConnectionEventData e, InterceptionResult r) => InterceptionResult.Suppress();
    public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection c, ConnectionEventData e, InterceptionResult r) => new(InterceptionResult.Suppress());

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r)
    { Capture(c); return InterceptionResult<DbDataReader>.SuppressWithResult(_probe.NewReader()); }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
    { Capture(c); return new(InterceptionResult<DbDataReader>.SuppressWithResult(_probe.NewReader())); }

    public InterceptionResult<int> NonQueryExecuting(DbCommand c, CommandEventData e, InterceptionResult<int> r)
    { Capture(c); return InterceptionResult<int>.SuppressWithResult(0); }

    public InterceptionResult<object> ScalarExecuting(DbCommand c, CommandEventData e, InterceptionResult<object> r)
    { Capture(c); return InterceptionResult<object>.SuppressWithResult(null); }

    // ... async variants of NonQuery and Scalar ...
}
```

It derives from `DbConnectionInterceptor` and implements `IDbCommandInterceptor` explicitly, because a single
type cannot inherit from both `DbConnectionInterceptor` and `DbCommandInterceptor`.

### The synthetic reader, and why it is shaped that way

The obvious implementation — a reader whose `Read()` always returns `false` — breaks in two ways that were
found empirically:

| Problem | Symptom | Fix |
| --- | --- | --- |
| Aggregate terminals materialize eagerly | `CountAsync`, `AnyAsync`, `MaxAsync`, `SumAsync` throw `InvalidOperationException: Sequence contains no elements` after capturing | yield **exactly one** synthetic row |
| Split queries need a wide reader | `The underlying reader doesn't have as many fields as expected. Expected: 2, actual: 0.` after capturing only the **first** command | report `FieldCount = 1024` |

`FieldCount` is 1024 specifically: narrower fails split queries, and `int.MaxValue` throws
`OutOfMemoryException` when a provider sizes a buffer from it.

The reader is tried on a **three-rung ladder**, and the rung that got furthest is reported as `captureMode`:

| Rung | Rows | String value | For |
| --- | --- | --- | --- |
| `SyntheticRow` | 1 | `""` | the normal case |
| `SyntheticRowTph` | 1 | a real discriminator value | TPH inheritance, where `""` is not a valid discriminator |
| `NoRows` | 0 | `""` | a last resort that always runs, at the cost of partial split-query capture |

With `SyntheticRow`, the `AsSplitQuery` scenario captures **2 of 2** commands and `CountAsync` does not throw.

### The `DbContext` activation ladder

`ContextActivator.Create` tries, in order, and records the winner in `activationStrategy`:

1. **`DerivedOnConfiguringContext`** — only when the generator emitted a subclass (a retry path, see below).
2. **`DesignTimeFactory`** — an `IDesignTimeDbContextFactory<TContext>` in the referenced assemblies. The
   produced context's resolved options are rebuilt with the interceptor added. Skipped when a dialect is
   forced, because the factory hard-codes a provider.
3. **`OptionsConstructor`** — a constructor taking only `DbContextOptions<TContext>`.
4. **`OptionsConstructorWithStubbedDependencies`** — options plus other services. `Stub.Make` supplies each
   remaining parameter via a `DispatchProxy` (for interfaces) or `Activator.CreateInstance`, and a warning
   names the stubbed types.

For the constructor rungs, options are built through `DbContextOptionsBuilder<TContext>` with the forced
provider, the inert connection string, `AddInterceptors(interceptor)`, and
`ConfigureWarnings(w => w.Ignore(ManyServiceProvidersCreatedWarning))`.

The `DesignTimeFactory` rung is the exception on one point: it *rebuilds* the options the factory already
resolved, so the interceptor and the warning suppression are added but the **factory's own connection string
is kept** — which in a real project is very likely a real one. Nothing opens it, and `Report.connectionString`
is passed through `Sanitize`, which masks `Password=` and `Pwd=` and nothing else. The verbose diagnostics
therefore expose the server, database and user name; that is worth knowing before attaching them to an issue.

A context with **no** `DbContextOptions` constructor that configures itself in `OnConfiguring` can only be
reached through a generated subclass exposing a `static CaptureInterceptor Pending` field. That subclass does
not compile for every context (`CS7036` when there is no parameterless constructor), so `PreviewRunner` tries
it **only after** an ordinary run has already failed with `ContextActivationFailed`, and keeps the original
result if the retry also fails.

### The `DbContext` discovery pass

The worker must **cast** the activated context to a compile-time type in order to write `__efspCtx.Products`,
so runtime discovery cannot serve the query itself. When the analyzer could not determine the context type,
`Generate` emits a **discovery program** instead, which lists every `DbContext` in the referenced assemblies.
`PreviewRunner` then re-runs automatically if exactly one candidate came back; otherwise the candidates are
reported.

### The JSON payload

```json
{
  "schemaVersion": 1,
  "success": true,
  "provider": "SqlServer",
  "efCoreVersion": "10.0.10.0",
  "contextType": "SampleShop.ShopDbContext",
  "activationStrategy": "DesignTimeFactory",
  "captureMode": "SyntheticRow",
  "commands": [
    {
      "sql": "SELECT [p].[Id], ...",
      "parameters": [
        { "name": "@minPrice", "dbType": "Decimal", "clrType": "decimal", "value": "100", "isNull": false }
      ]
    }
  ],
  "result": {
    "isAsync": true, "shape": "List", "elementType": "ProductDto",
    "elementKind": "Dto", "declaredResultType": "List<ProductDto>"
  },
  "warnings": [],
  "error": null,
  "errorKind": "None"
}
```

Written between `<<<EFSQLPREVIEW-BEGIN>>>` and `<<<EFSQLPREVIEW-END>>>` on stdout. The wire format is fixed by
`PreviewResponse.JsonOptions`: camelCase names, case-insensitive reads, nulls omitted, relaxed escaping, and
enums as PascalCase strings.

The worker exits **0 whenever a payload exists**, including a failed one. A non-zero exit is the extension's
signal that the build itself broke and there is nothing to parse.

---

## Stage 6 — parse and render

`WorkerOutputParser` extracts the payload (last `BEGIN` wins, so a sentinel echoed inside a compiler-error line
cannot fool it) and classifies failures into a `PreviewErrorKind`:

| Kind | Detected from |
| --- | --- |
| `CompileError` | `CSxxxx` diagnostics in the build output |
| `ProviderVersionMismatch` | `NU1102`/`NU1101` restore failures — checked **before** compile errors, since a restore failure is shaped like one |
| `ContextActivationFailed` | the worker's own `PreviewActivationException` |
| `NotTranslatable` | EF's translation failure message |
| `ProviderError` | the provider rejecting the command |
| `NoPayload` | the process ran but emitted no sentinels |
| `Timeout` | the process tree was killed |

`DiagnosticRemapper` rewrites any diagnostic whose path is the generated worker and whose line has a `LineMap`
entry, so it points at the user's document instead. Remapping is **statement-granular**: a diagnostic maps to
the free variable's declaration span or the start of the query, never to a column inside it.

The tool window then renders SQL, parameters, the result-shape sentence, the variables panel and the
diagnostics.

---

## A complete generated worker

This is the real file produced for the DTO-projection example, at
`%LOCALAPPDATA%\EFCoreSqlPreview\scratch\PreviewEndToEndQueries-<hash>\worker.cs`. The harness types below the
top-level statements are elided; the file is 768 lines in total.

```csharp
// <auto-generated> EF Core SQL Preview worker. Regenerated on every run; do not edit. </auto-generated>
#:project C:\Users\Dell\source\repos\EFCoreSqlPreview\samples\SampleShop\SampleShop.csproj

#:property PublishAot=false
#:property Nullable=disable
#:property TreatWarningsAsErrors=false
#:property EnableNETAnalyzers=false
#:property AnalysisMode=None
#:property GenerateDocumentationFile=false
#:property NoWarn=$(NoWarn);CS0168;CS0219;CS1998;CS8600;CS8602;CS8603;CS8604;CS8618;CS8625;NU1608;NU1701;NU1903

using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using SampleShop;

// ---------------------------------------------------------------------------------------------------------
// Top-level statements must precede every type declaration (CS8803), so the harness types are at the bottom.
// Never introduce an identifier named `args`: top-level statements already declare one.
// ---------------------------------------------------------------------------------------------------------
var __efspReport = await Preview.RunAsync(
    contextType: typeof(ShopDbContext),
    body: async (__efspRawContext, __efspProbe) =>
    {
        var __efspCtx = (ShopDbContext)__efspRawContext;

        decimal minPrice = 100m;

        return await __efspProbe.ObserveAsync(() => __efspCtx.Products
            .Where(p => p.Price > minPrice)
            .Select(p => new ProductDto
            {
            Id = p.Id,
            Name = p.Name,
            Price = p.Price,
            CategoryName = p.Category!.Name,
            })
            .ToListAsync());
    });

// Exit code 0 whenever a payload exists, including a failed one: a non-zero exit is the extension's signal
// that the build itself broke and there is nothing to parse.
Preview.Emit(__efspReport);
return 0;

public static class Preview
{
    public const string Begin = "<<<EFSQLPREVIEW-BEGIN>>>";
    public const string End = "<<<EFSQLPREVIEW-END>>>";

    // Deliberately not const: a const null would make the dialect checks compile-time constants and every
    // branch behind them unreachable code, which litters stdout with CS0162 next to the payload.
    public static readonly string Dialect = null;
    public const string Neutral = "Server=.;Database=EFCoreSqlPreview;Trusted_Connection=True;TrustServerCertificate=True";

    public static void ConfigureProvider(DbContextOptionsBuilder b) => b.UseSqlServer(Neutral);

    // ... RunAsync (the three-rung ladder), Emit, Render, Unwrap ...
}

// ... Probe, CaptureInterceptor, FakeReader, ContextActivator, Stub, NullProxy, Report ...
```

Three things are worth noticing:

- **The user's query is verbatim**, apart from `this.db` → `__efspCtx`. No rewriting, no reinterpretation.
- **`minPrice` is a real local**, so EF's closure-parameterisation sees exactly what it would see at runtime.
  That is why the parameter comes back named `@minPrice`.
- **`ObserveAsync`** wraps the query so the harness can record the awaited result's runtime type — which is
  where `shape`, `elementType` and `elementKind` come from.

You can run this file by hand at any time:

```powershell
dotnet run --file "$env:LOCALAPPDATA\EFCoreSqlPreview\scratch\<document>-<hash>\worker.cs" --tl:off
```

---

## Design decisions and the evidence behind them

| Decision | Why |
| --- | --- |
| **Syntax-only Roslyn, no semantic model** | A semantic model needs the project loaded (`MSBuildWorkspace`), which costs seconds and a large memory footprint. The generated worker's compilation does the semantic work instead, and does it more accurately. |
| **File-based app rather than a generated `.csproj`** | `#:project` is one line. A generated project needs a directory, a restore, and careful isolation from the user's `Directory.Build.props`. |
| **Out-of-process VSIX** | The command shells out to `dotnet run` and waits seconds. In-process, that is an IDE-freezing operation. |
| **`Core` targets `netstandard2.0`** | Keeps the logic host-agnostic and testable without Visual Studio. `net8.0` is added purely so the VSIX resolves an asset that gets `System.Text.Json` from the shared framework rather than packing a second copy. |
| **One synthetic row, `FieldCount = 1024`** | Established empirically: zero rows makes every aggregate terminal throw; a narrow reader truncates split-query capture. |
| **`PublishAot=false`** | EF Core refuses to build the model without it. |
| **Restore failures classified before compile failures** | `NU1102` is shaped like a compiler diagnostic but means the dialect has no matching package version — a completely different message for the user. |
| **Derived `OnConfiguring` subclass is a retry, not a default** | It fails to compile (`CS7036`) for any context without a parameterless constructor, so making it the default would break the common case. |
| **Scratch under `%LOCALAPPDATA%`, never the repo** | The generated program must not appear in the user's `git status`, and warm artifacts are what make a re-run 2 seconds. |
| **`Preview.Dialect` is `static readonly`, not `const`** | A `const null` makes the dialect branches compile-time-unreachable, which sprays `CS0162` warnings onto stdout beside the payload. |
| **SELECT queries only** | Previewing a write means either executing it or reimplementing EF's update pipeline. The first is unacceptable in an editor tool; the second is a different product. |

---

## Performance

Measured on this repository, .NET SDK 10.0.302, EF Core 10.0.10.

| Stage | Cost |
| --- | --- |
| `CSharpSyntaxTree.ParseText` on a 236 KB document | ~20 ms |
| `LinqSelectionAnalyzer.Analyze` with a cached tree | **~0.1 ms** |
| Fast unit test suite (757 tests) | 0.7 s |
| Warm end-to-end run (`dotnet run --file`, unchanged worker) | **2.3 – 3.0 s** |
| Cold run (restore + build of the user's project) | seconds to minutes; the default timeout is 120 s |

The analysis is effectively free. The whole cost is the build, which is why the scratch directory is keyed and
identical writes are skipped — the second run of the same query reuses everything.

Because analysis is so cheap and parsing is not, the VSIX should prefer the
`Analyze(SyntaxTree, TextSpan, AnalyzerOptions)` overload and cache trees per document version.
