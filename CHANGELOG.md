# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

## [1.0.0] - 2026-07-27

Initial release.

### Added

- **Editor command "Preview EF Core SQL"** on the C# editor context menu, on the Extensions menu, and bound to
  the two-chord shortcut <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Q</kbd>, <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>S</kbd>
  inside the text editor. Visible only in `.cs` documents; enabled once the solution is fully loaded.
- **SQL preview without a database and without running the application.** An EF Core interceptor suppresses
  `ConnectionOpening`/`ConnectionOpeningAsync` and captures the fully built `DbCommand` from
  `ReaderExecuting`, `ScalarExecuting` and `NonQueryExecuting`, returning synthetic results instead of
  executing.
- **Forgiving selection handling.** A bare caret, a partial chain, a whole statement, a `return await …;`, or a
  multi-statement query-builder block all resolve to the right query. Query syntax with no terminal operator
  gets one supplied, and the result line says so.
- **Free-variable reproduction.** Variables the query captures are reproduced from their declarations when the
  initializer is safely reproducible (literals, `const`, simple `new`, collection expressions, well-known
  statics). Anything else gets a synthesized default and is flagged in an editable Variables panel with a
  Re-run button.
- **Custom `IQueryable` extension methods** resolve, because the generated preview program is compiled against
  the user's project by the real C# compiler.
- **Full terminal-operator catalogue.** `ToList`, `ToArray`, `ToDictionary`, `ToHashSet`, `First`,
  `FirstOrDefault`, `Single`, `SingleOrDefault`, `Last`, `LastOrDefault`, `ElementAt`, `ElementAtOrDefault`,
  `Count`, `LongCount`, `Any`, `All`, `Sum`, `Average`, `Min`, `Max`, `Contains` and `Load`, each with its
  `Async` variant; plus `ToLookup`, `AsEnumerable` and `ForEachAsync`/`AsAsyncEnumerable`, which exist in only
  one form.
- **Result-shape reporting**: async vs sync, awaited or not, and `List` / `Array` / `Dictionary` (with key
  type) / `HashSet` / `Lookup` / `FirstElement` / `SingleElement` / `Scalar` / `Boolean` / deferred, plus the
  element kind — entity, DTO, anonymous, tuple, scalar or grouping.
- **Parameters table** showing name, DB type, CLR type and value for every captured parameter.
- **Multi-command capture.** `AsSplitQuery` and `Include`-driven splits capture every command EF issues, not
  just the first. A wide synthetic single-row reader (`FieldCount` 1024) is what makes this and eager
  aggregate terminals work.
- **Provider auto-detection** for `Microsoft.EntityFrameworkCore.SqlServer`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Sqlite`,
  `Pomelo.EntityFrameworkCore.MySql` and `Oracle.EntityFrameworkCore`. Resolves `$(Property)` versions from
  the `Directory.Build.props` chain and central versions from `Directory.Packages.props`, follows
  `ProjectReference` chains transitively, and falls back to scanning the nearest `.sln`/`.slnx` for a
  provider-bearing sibling project.
- **Dialect picker** in the tool window, so the same query can be re-rendered through a different provider for
  comparison.
- **`DbContext` activation ladder** with the strategy reported in the header line:
  `IDesignTimeDbContextFactory<T>`, then a `DbContextOptions<T>` constructor, then an options constructor with
  stubbed extra dependencies, then a generated `OnConfiguring` subclass as a retry.
- **`DbContext` discovery pass.** When the context type cannot be determined syntactically, the worker lists
  every candidate in the referenced assemblies; a single candidate is used automatically.
- **Compiler diagnostics remapped** from the generated program back onto the user's own document.
- **Tool window** with SQL, Parameters, Variables, Query, Diagnostics and Generated program tabs, a Re-run and
  Cancel button, Copy SQL / Copy all, a verbose toggle, and a live status line.
- **Settings** at `%LOCALAPPDATA%\EFCoreSqlPreview\settings.json`: `TimeoutSeconds` (10-900, default 120),
  `DotnetPath`, `ProviderOverride`, `VerboseMode`.
- **`samples/SampleShop`** — a net10.0 EF Core 10 fixture with owned types, value-converted collections,
  two-way navigations, a design-time factory, custom queryable extensions and seven ready-to-select queries.
- **766 tests**: 757 fast unit tests plus 9 end-to-end tests that really run `dotnet run --file` against the
  sample and assert on the SQL that comes back.

### Known limitations

- **SELECT (read) queries only.** `ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`, `FromSql*`, `ExecuteSql*`,
  entity mutation calls and third-party bulk operators are detected and reported as out of scope.
- The tool window has **no picker for choosing among several `DbContext` candidates** and no project picker.
  `EFCoreSqlPreview.Core` supports both overrides; the UI does not surface them yet.
- **MySQL and Oracle dialects have no EF Core 10 package**, so forcing them on an EF Core 10 project fails with
  `ProviderVersionMismatch`. The generator warns before running.
- **AutoMapper's `ProjectTo<T>()` is not supported**; it needs a configured `IConfigurationProvider` that only
  exists inside the application's DI container.
- **The first run pays a restore and build** of the user's project. Warm re-runs are 2-3 seconds.
- **A preview builds the user's project**, refreshing its `bin\` and `obj\` in place, and therefore requires
  that project to compile. Nothing is written into the source tree; the generated program lives under
  `%LOCALAPPDATA%\EFCoreSqlPreview\scratch\`.
- **When an `IDesignTimeDbContextFactory<T>` wins the activation ladder its own connection string is used**,
  not the inert placeholder. It is never opened, and `Password=`/`Pwd=` are masked in the payload, but the
  server, database and user name appear in the Verbose diagnostics.
- Only **EF Core 10.x** is verified end to end. Earlier majors are expected to work but are untested.
- TPH discriminators and `OwnsOne(...).ToJson()` exercise capture-reader fallback rungs that no test covers.

[Unreleased]: https://github.com/kerols1234/EFCoreSqlPreview/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/kerols1234/EFCoreSqlPreview/releases/tag/v1.0.0
