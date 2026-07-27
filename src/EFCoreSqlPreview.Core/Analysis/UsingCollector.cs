using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFCoreSqlPreview.Core.Analysis;

/// <summary>
/// Gathers the using directives and namespace context the generated worker must reproduce.
/// </summary>
/// <remarks>
/// Global usings do not cross assembly boundaries, so the worker inherits none of the user's. Every collected
/// directive must be re-emitted, including the <c>global</c> ones (with the modifier stripped).
/// </remarks>
public static class UsingCollector
{
    /// <summary>Collects usings from the compilation unit and every namespace enclosing the query.</summary>
    /// <param name="root">The syntax root of the document.</param>
    /// <param name="queryNode">The resolved query node, used to walk enclosing namespaces.</param>
    /// <returns>The collected directives and namespace chain.</returns>
    public static UsingsInfo Collect(SyntaxNode root, SyntaxNode queryNode)
    {
        if (root is null)
        {
            return UsingsInfo.Empty;
        }

        var directives = new List<UsingDirectiveInfo>();

        if (root is CompilationUnitSyntax unit)
        {
            AddAll(unit.Usings, directives);
        }

        // Namespaces are walked outermost first so the emitted order matches the source.
        var namespaces = queryNode is null
            ? EnumerateTopLevelNamespaces(root)
            : queryNode.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().ToList();

        foreach (var declaration in namespaces)
        {
            AddAll(declaration.Usings, directives);
        }

        var containingNamespace = BuildNamespaceName(namespaces);
        return new UsingsInfo(directives, containingNamespace, BuildNamespaceChain(containingNamespace));
    }

    private static IReadOnlyList<BaseNamespaceDeclarationSyntax> EnumerateTopLevelNamespaces(SyntaxNode root)
        => root is CompilationUnitSyntax unit
            ? unit.Members.OfType<BaseNamespaceDeclarationSyntax>().ToList()
            : Array.Empty<BaseNamespaceDeclarationSyntax>();

    private static void AddAll(SyntaxList<UsingDirectiveSyntax> usings, List<UsingDirectiveInfo> directives)
    {
        foreach (var directive in usings)
        {
            directives.Add(Describe(directive));
        }
    }

    private static UsingDirectiveInfo Describe(UsingDirectiveSyntax directive)
    {
        var name = directive.Name?.ToString() ?? directive.NamespaceOrType?.ToString() ?? string.Empty;
        var text = directive.ToString().Trim();
        if (!text.EndsWith(";", StringComparison.Ordinal))
        {
            text += ";";
        }

        return new UsingDirectiveInfo(
            name,
            directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword),
            directive.StaticKeyword.IsKind(SyntaxKind.StaticKeyword),
            directive.Alias?.Name.Identifier.ValueText,
            text);
    }

    private static string? BuildNamespaceName(IReadOnlyList<BaseNamespaceDeclarationSyntax> namespaces)
    {
        if (namespaces.Count == 0)
        {
            return null;
        }

        var parts = new List<string>(namespaces.Count);
        foreach (var declaration in namespaces)
        {
            var name = declaration.Name.ToString().Trim();
            if (name.Length > 0)
            {
                parts.Add(name);
            }
        }

        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private static IReadOnlyList<string> BuildNamespaceChain(string? containingNamespace)
    {
        if (string.IsNullOrEmpty(containingNamespace))
        {
            return Array.Empty<string>();
        }

        // Namespace nesting lookup does not apply across assemblies, so the worker needs every prefix, not
        // just the innermost namespace.
        var segments = containingNamespace!.Split('.');
        var chain = new List<string>(segments.Length);
        var current = string.Empty;
        foreach (var segment in segments)
        {
            current = current.Length == 0 ? segment : current + "." + segment;
            chain.Add(current);
        }

        return chain;
    }
}
