using EFCoreSqlPreview.Core.Analysis;

namespace EFCoreSqlPreview.Core.Tests.Analysis;

/// <summary>
/// Assertion helpers that report the whole diagnostic list on failure, so a red test says why.
/// </summary>
internal static class AnalysisAssertions
{
    /// <summary>Asserts a diagnostic id was reported.</summary>
    /// <param name="result">The analysis result.</param>
    /// <param name="id">The expected diagnostic id.</param>
    public static void ShouldHaveDiagnostic(this QueryAnalysisResult result, string id)
        => result.Diagnostics.Select(d => d.Id).ShouldContain(id, Describe(result));

    /// <summary>Asserts a diagnostic id was not reported.</summary>
    /// <param name="result">The analysis result.</param>
    /// <param name="id">The diagnostic id that must be absent.</param>
    public static void ShouldNotHaveDiagnostic(this QueryAnalysisResult result, string id)
        => result.Diagnostics.Select(d => d.Id).ShouldNotContain(id, Describe(result));

    /// <summary>Returns the free variable with a given name, failing helpfully when it is missing.</summary>
    /// <param name="result">The analysis result.</param>
    /// <param name="name">The variable name.</param>
    /// <returns>The free variable.</returns>
    public static FreeVariable Variable(this QueryAnalysisResult result, string name)
    {
        var variable = result.FreeVariables.FirstOrDefault(v => v.Name == name);
        variable.ShouldNotBeNull("free variables were: " + string.Join(", ", result.FreeVariables.Select(v => v.Name)));
        return variable!;
    }

    /// <summary>The names of the free variables, in reported order.</summary>
    /// <param name="result">The analysis result.</param>
    /// <returns>The variable names.</returns>
    public static string[] VariableNames(this QueryAnalysisResult result)
        => result.FreeVariables.Select(v => v.Name).ToArray();

    /// <summary>The method names of the resolved chain, head-first.</summary>
    /// <param name="result">The analysis result.</param>
    /// <returns>The chain call names.</returns>
    public static string[] ChainNames(this QueryAnalysisResult result)
        => result.Chain.Select(c => c.Name).ToArray();

    /// <summary>Renders the result's diagnostics for use as an assertion message.</summary>
    /// <param name="result">The analysis result.</param>
    /// <returns>A human-readable diagnostic dump.</returns>
    public static string Describe(this QueryAnalysisResult result)
        => "status=" + result.Status
            + "; query=" + result.QueryExpression
            + "; diagnostics=["
            + string.Join(" | ", result.Diagnostics.Select(d => d.Id + " " + d.Severity + " " + d.Message))
            + "]";
}
