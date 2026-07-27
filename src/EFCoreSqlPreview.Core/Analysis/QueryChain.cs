using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// One invocation in the query's method chain, reported in source order.
/// </summary>
/// <param name="Name">The method name, e.g. <c>Where</c>.</param>
/// <param name="ArgumentCount">Number of arguments at the call site.</param>
/// <param name="TypeArguments">Explicit type arguments, e.g. <c>["Product"]</c> for <c>Set&lt;Product&gt;()</c>.</param>
/// <param name="Span">Span of the invocation in the user's document.</param>
public sealed record ChainCallInfo(
    string Name,
    int ArgumentCount,
    IReadOnlyList<string> TypeArguments,
    TextSpan Span);

/// <summary>
/// The decomposed method chain of a selected query: its head expression and the calls applied to it.
/// </summary>
/// <param name="Root">The outermost expression the selection resolved to, including any <c>await</c>.</param>
/// <param name="Head">The leftmost receiver, e.g. <c>_db</c> or the <c>from</c> clause source.</param>
/// <param name="IsAwaited">Whether <paramref name="Root"/> is an <c>await</c> expression.</param>
/// <param name="Calls">The invocations applied to <paramref name="Head"/>, head-first.</param>
/// <param name="IsQuerySyntax">Whether the selection is a <c>from ... select ...</c> query expression.</param>
public sealed record QueryChain(
    ExpressionSyntax Root,
    ExpressionSyntax Head,
    bool IsAwaited,
    IReadOnlyList<InvocationExpressionSyntax> Calls,
    bool IsQuerySyntax);
