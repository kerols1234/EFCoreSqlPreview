using EFCoreSqlPreview.Core.Projects;
using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Execution;

/// <summary>
/// Everything the extension captures at the moment the user invokes the command, plus any choices they have
/// since made in the tool window.
/// </summary>
/// <param name="DocumentText">The full document text including unsaved edits.</param>
/// <param name="DocumentPath">Absolute path to the document.</param>
/// <param name="Selection">The raw selection span from the editor; normalisation happens in the analyzer.</param>
public sealed record PreviewRequest(
    string DocumentText,
    string DocumentPath,
    TextSpan Selection)
{
    /// <summary>
    /// The dialect chosen in the picker. <see langword="null"/> means use whatever the project references.
    /// </summary>
    public EfProvider? DialectOverride { get; init; }

    /// <summary>The DbContext type the user picked, when auto-detection found zero or several candidates.</summary>
    public string? DbContextTypeOverride { get; init; }

    /// <summary>Values the user typed into the free-variables panel, keyed by variable name.</summary>
    public IReadOnlyDictionary<string, string> FreeVariableOverrides { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Overrides project discovery. Used by tests and by the rare case where the document's nearest
    /// <c>.csproj</c> is not the project to build against.
    /// </summary>
    public string? ProjectPathOverride { get; init; }

    /// <summary>
    /// How long to let the worker run before killing its whole process tree. A cold NuGet restore can be slow,
    /// so this is generous.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Whether to keep the generated worker source on disk for the "Generated program" tab.</summary>
    public bool KeepGeneratedSource { get; init; } = true;
}
