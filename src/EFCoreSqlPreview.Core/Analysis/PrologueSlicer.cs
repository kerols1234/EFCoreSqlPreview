using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Selects the statements preceding a query that must be replayed for it to make sense.
/// </summary>
/// <remarks>
/// Backward liveness, not semantic reasoning: the kept statements are copied into the worker verbatim. That is
/// the only approach that survives query building through <c>if</c>, <c>switch</c> and loops.
/// </remarks>
public static class PrologueSlicer
{
    /// <summary>Slices the statements a query depends on out of its enclosing block.</summary>
    /// <param name="block">The block containing the query.</param>
    /// <param name="anchor">The statement the query lives in; the sweep starts just before it.</param>
    /// <param name="seed">The names that are live at the anchor, typically the query-builder local.</param>
    /// <returns>The kept statements in source order.</returns>
    public static IReadOnlyList<StatementSyntax> Slice(BlockSyntax block, StatementSyntax anchor, ISet<string> seed)
    {
        if (block is null || anchor is null || seed is null || seed.Count == 0)
        {
            return Array.Empty<StatementSyntax>();
        }

        var live = new HashSet<string>(seed, StringComparer.Ordinal);

        var preceding = new List<StatementSyntax>();
        foreach (var statement in block.Statements)
        {
            if (statement == anchor)
            {
                break;
            }

            preceding.Add(statement);
        }

        var kept = new List<StatementSyntax>();
        for (var i = preceding.Count - 1; i >= 0; i--)
        {
            var statement = preceding[i];
            var writes = Writes(statement);
            if (!Overlaps(writes, live))
            {
                continue;
            }

            kept.Insert(0, statement);
            foreach (var read in Reads(statement))
            {
                live.Add(read);
            }
        }

        return kept;
    }

    /// <summary>The names a statement assigns to: declarators, simple assignments and loop variables.</summary>
    /// <param name="statement">The statement to inspect.</param>
    /// <returns>The written names.</returns>
    public static ISet<string> Writes(StatementSyntax statement)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        if (statement is null)
        {
            return written;
        }

        foreach (var node in statement.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    written.Add(declarator.Identifier.ValueText);
                    break;

                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax target }:
                    written.Add(target.Identifier.ValueText);
                    break;

                case ForEachStatementSyntax forEach:
                    written.Add(forEach.Identifier.ValueText);
                    break;

                case SingleVariableDesignationSyntax designation:
                    written.Add(designation.Identifier.ValueText);
                    break;
            }
        }

        return written;
    }

    /// <summary>
    /// The names a statement reads. Member names, invocation targets, bound lambda parameters and the
    /// contextual keyword <c>var</c> are excluded, or the live set fills with phantom variables.
    /// </summary>
    /// <param name="statement">The statement to inspect.</param>
    /// <returns>The read names.</returns>
    public static ISet<string> Reads(StatementSyntax statement)
    {
        var read = new HashSet<string>(StringComparer.Ordinal);
        if (statement is null)
        {
            return read;
        }

        var bound = FreeVariableCollector.CollectBoundNames(statement);

        foreach (var identifier in statement.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var name = identifier.Identifier.ValueText;
            if (name.Length == 0 || name == "var" || bound.Contains(name))
            {
                continue;
            }

            if (identifier.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == identifier)
            {
                continue;
            }

            if (identifier.Parent is InvocationExpressionSyntax invocation && invocation.Expression == identifier)
            {
                continue;
            }

            read.Add(name);
        }

        return read;
    }

    private static bool Overlaps(ISet<string> writes, ICollection<string> live)
    {
        foreach (var name in writes)
        {
            if (live.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
