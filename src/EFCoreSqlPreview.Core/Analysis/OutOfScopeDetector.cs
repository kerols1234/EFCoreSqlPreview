using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Detects the write-side and raw-SQL constructs EF Core SQL Preview refuses to preview.
/// </summary>
/// <remarks>
/// Detection is name-based, so a user method genuinely called <c>ExecuteDelete</c> on an unrelated type will
/// false-positive. That is accepted: the message names the exact span, so the reason is visible.
/// </remarks>
public static class OutOfScopeDetector
{
    private const string SelectOnly = "EF Core SQL Preview previews SELECT queries only.";

    /// <summary>Every method name that triggers detection, mapped to the reason it triggers.</summary>
    public static IReadOnlyDictionary<string, OutOfScopeReason> KnownConstructs { get; } =
        new Dictionary<string, OutOfScopeReason>(StringComparer.Ordinal)
        {
            ["ExecuteUpdate"] = OutOfScopeReason.ExecuteUpdate,
            ["ExecuteUpdateAsync"] = OutOfScopeReason.ExecuteUpdate,
            ["ExecuteDelete"] = OutOfScopeReason.ExecuteDelete,
            ["ExecuteDeleteAsync"] = OutOfScopeReason.ExecuteDelete,
            ["SaveChanges"] = OutOfScopeReason.SaveChanges,
            ["SaveChangesAsync"] = OutOfScopeReason.SaveChanges,
            ["FromSql"] = OutOfScopeReason.RawSql,
            ["FromSqlRaw"] = OutOfScopeReason.RawSql,
            ["FromSqlInterpolated"] = OutOfScopeReason.RawSql,
            ["ExecuteSql"] = OutOfScopeReason.RawSql,
            ["ExecuteSqlAsync"] = OutOfScopeReason.RawSql,
            ["ExecuteSqlRaw"] = OutOfScopeReason.RawSql,
            ["ExecuteSqlRawAsync"] = OutOfScopeReason.RawSql,
            ["ExecuteSqlInterpolated"] = OutOfScopeReason.RawSql,
            ["ExecuteSqlInterpolatedAsync"] = OutOfScopeReason.RawSql,
            ["Add"] = OutOfScopeReason.Mutation,
            ["AddAsync"] = OutOfScopeReason.Mutation,
            ["AddRange"] = OutOfScopeReason.Mutation,
            ["AddRangeAsync"] = OutOfScopeReason.Mutation,
            ["Update"] = OutOfScopeReason.Mutation,
            ["UpdateRange"] = OutOfScopeReason.Mutation,
            ["Remove"] = OutOfScopeReason.Mutation,
            ["RemoveRange"] = OutOfScopeReason.Mutation,
            ["Attach"] = OutOfScopeReason.Mutation,
            ["AttachRange"] = OutOfScopeReason.Mutation,
            ["BulkInsert"] = OutOfScopeReason.ThirdPartyBulk,
            ["BulkInsertAsync"] = OutOfScopeReason.ThirdPartyBulk,
            ["BulkUpdate"] = OutOfScopeReason.ThirdPartyBulk,
            ["BulkUpdateAsync"] = OutOfScopeReason.ThirdPartyBulk,
            ["BulkDelete"] = OutOfScopeReason.ThirdPartyBulk,
            ["BulkDeleteAsync"] = OutOfScopeReason.ThirdPartyBulk,
            ["BatchUpdate"] = OutOfScopeReason.ThirdPartyBulk,
            ["BatchDelete"] = OutOfScopeReason.ThirdPartyBulk,
        };

    /// <summary>Scans a node and its descendants for out-of-scope calls.</summary>
    /// <param name="scope">The query node plus, separately, every prologue statement.</param>
    /// <param name="contextRoot">
    /// The resolved context root. Mutation names only count when invoked on the context or one of its DbSets,
    /// because <c>Add</c> and <c>Remove</c> are also ordinary collection methods.
    /// </param>
    /// <param name="options">Analysis options, which may add house-specific names.</param>
    /// <returns><see cref="OutOfScopeInfo.None"/> when nothing was found.</returns>
    public static OutOfScopeInfo Detect(SyntaxNode scope, ContextRootInfo contextRoot, AnalyzerOptions options)
    {
        if (scope is null)
        {
            return OutOfScopeInfo.None;
        }

        options ??= AnalyzerOptions.Default;

        OutOfScopeInfo? softMatch = null;

        foreach (var invocation in scope.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = QueryChainWalker.CallName(invocation);
            if (name is null)
            {
                continue;
            }

            if (!KnownConstructs.TryGetValue(name, out var reason))
            {
                if (!Contains(options.AdditionalOutOfScopeMethods, name))
                {
                    continue;
                }

                reason = OutOfScopeReason.ThirdPartyBulk;
            }

            if (reason == OutOfScopeReason.Mutation && !IsOnTheContext(invocation, contextRoot))
            {
                continue;
            }

            var span = SpanOfName(invocation);
            var isHardBlock = IsHardBlock(reason, name, invocation);
            var info = new OutOfScopeInfo(reason, name, BuildMessage(reason, name), span, isHardBlock);

            if (isHardBlock)
            {
                return info;
            }

            softMatch ??= info;
        }

        return softMatch ?? OutOfScopeInfo.None;
    }

    private static bool IsHardBlock(OutOfScopeReason reason, string name, InvocationExpressionSyntax invocation)
    {
        if (reason != OutOfScopeReason.RawSql)
        {
            return true;
        }

        // FromSql* composed into a longer LINQ chain still makes EF generate real SQL around the fragment, so
        // it is worth previewing. ExecuteSql* runs an arbitrary command and never is.
        if (!name.StartsWith("FromSql", StringComparison.Ordinal))
        {
            return true;
        }

        return invocation.Parent is not MemberAccessExpressionSyntax memberAccess
            || memberAccess.Expression != invocation;
    }

    private static bool IsOnTheContext(InvocationExpressionSyntax invocation, ContextRootInfo? contextRoot)
    {
        var identifier = contextRoot?.IdentifierName;
        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return false;
        }

        // Walk to the leftmost receiver: '_db.Products.Add(x)' is a mutation, 'list.Add(x)' is not.
        var receiver = memberAccess.Expression;
        while (true)
        {
            switch (receiver)
            {
                case MemberAccessExpressionSyntax nested:
                    receiver = nested.Expression;
                    continue;
                case InvocationExpressionSyntax nestedInvocation:
                    receiver = nestedInvocation.Expression;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    receiver = parenthesized.Expression;
                    continue;
            }

            break;
        }

        return receiver switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText == identifier,
            ThisExpressionSyntax => identifier == "this",
            _ => false,
        };
    }

    private static bool Contains(IReadOnlyList<string>? names, string name)
    {
        if (names is null)
        {
            return false;
        }

        foreach (var candidate in names)
        {
            if (string.Equals(candidate, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan SpanOfName(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Span,
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.Span,
            SimpleNameSyntax simpleName => simpleName.Span,
            _ => invocation.Span,
        };

    private static string BuildMessage(OutOfScopeReason reason, string name)
        => reason switch
        {
            OutOfScopeReason.ExecuteUpdate =>
                $"{SelectOnly} '{name}' generates an UPDATE statement, which this tool does not preview.",
            OutOfScopeReason.ExecuteDelete =>
                $"{SelectOnly} '{name}' generates a DELETE statement, which this tool does not preview.",
            OutOfScopeReason.SaveChanges =>
                $"{SelectOnly} '{name}' writes to the database and is not previewed.",
            OutOfScopeReason.RawSql when name.StartsWith("FromSql", StringComparison.Ordinal) =>
                $"This query uses raw SQL ('{name}'). That fragment is written by hand; only the SQL EF Core composes around it is previewed.",
            OutOfScopeReason.RawSql =>
                $"'{name}' runs an arbitrary command. {SelectOnly}",
            OutOfScopeReason.Mutation =>
                $"{SelectOnly} '{name}' stages a change on the context and is not previewed.",
            OutOfScopeReason.ThirdPartyBulk =>
                $"{SelectOnly} Third-party bulk operations such as '{name}' are not previewed.",
            _ => SelectOnly,
        };
}
