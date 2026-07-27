---
name: Feature request
about: Suggest a capability or an improvement
title: ''
labels: enhancement
assignees: ''
---

## The problem

<!--
What are you trying to find out about a query that the tool cannot tell you today?
Describe the situation, not the solution.
-->

## What you would like

<!-- The behaviour you want. If it is a UI change, describe where it lives. -->

## A concrete example

<!-- A query, a project shape, or a workflow that would benefit. Real code beats a description. -->

```csharp

```

## Alternatives you have considered

<!-- Including "I currently do X by hand", which is useful signal. -->

## Scope check

The tool is deliberately narrow. These are **out of scope by design** and will be closed as such:

- Previewing writes: `ExecuteUpdate`, `ExecuteDelete`, `SaveChanges`, `Add`/`Update`/`Remove`, third-party bulk
  operators. Previewing a write means either executing it or reimplementing EF's update pipeline.
- Anything that contacts a database: connecting, validating against a schema, showing an execution plan,
  measuring performance.
- Running the user's application, host builder or DI container.

If your request touches one of those, say why it is worth revisiting anyway - the boundary is a judgement, not
a law.

## Anything else

<!-- Related issues, prior art in other tools, links to EF Core docs. -->
