---
name: Bug report
about: The preview produced the wrong SQL, failed, or did not run
title: ''
labels: bug
assignees: ''
---

## What happened

<!-- One or two sentences. -->

## What you expected

<!-- The SQL, parameters or result shape you expected instead. -->

## The query

<!--
The LINQ query you selected, and what you selected exactly (whole chain? caret only? a partial selection?).
Reduce it if you can - a minimal query that still reproduces is far easier to fix.
-->

```csharp

```

Anything the query depends on that is not obvious from it - custom `IQueryable` extension methods, the
`DbContext` and `DbSet` declarations, the DTO - helps a lot:

```csharp

```

## What the tool showed

<!-- The error banner and status line, plus the SQL if any came back. -->

```

```

## Diagnostics tab

<!--
Turn on the "Verbose" checkbox in the tool window, re-run, then paste the whole Diagnostics tab.
This is usually the single most useful thing in the report.
-->

<details>
<summary>Diagnostics</summary>

```

```

</details>

## Generated program

<!--
The "Generated program" tab, or the file at
%LOCALAPPDATA%\EFCoreSqlPreview\scratch\<document>-<hash>\worker.cs

Please redact anything sensitive - it contains your query and your variable values.
-->

<details>
<summary>worker.cs</summary>

```csharp

```

</details>

## Environment

| | |
| --- | --- |
| Extension version | <!-- e.g. 1.0.0 --> |
| Visual Studio | <!-- Help > About, e.g. 17.14.5 --> |
| .NET SDK | <!-- output of `dotnet --list-sdks` --> |
| EF Core version | <!-- e.g. 10.0.10 --> |
| EF provider | <!-- SQL Server / PostgreSQL / SQLite / MySQL / Oracle --> |
| Dialect picker set to | <!-- Auto, or a specific provider --> |
| Target framework of your project | <!-- e.g. net9.0 --> |

## Checklist

- [ ] The query is a **SELECT (read) query** - `ExecuteUpdate`, `ExecuteDelete`, `SaveChanges` and raw SQL are
      out of scope by design.
- [ ] The document had no red squiggles around the selection (the analyzer parses syntax and a broken file
      breaks it).
- [ ] I ran it a second time (the first run after a project change restores and builds).
- [ ] I checked the [Troubleshooting](https://github.com/kerols1234/EFCoreSqlPreview#troubleshooting) table.
