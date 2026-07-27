namespace EFCoreSqlPreview.Core.Projects;

/// <summary>
/// The user project a previewed query belongs to, and what the worker needs to reference it.
/// </summary>
/// <param name="ProjectPath">Absolute path to the <c>.csproj</c> owning the document.</param>
/// <param name="ProjectName">The project's file name without extension.</param>
/// <param name="Provider">The detected EF Core provider.</param>
/// <param name="ProviderPackageVersion">
/// The provider package version as written in the project, or <see langword="null"/> when it comes from
/// central package management and could not be resolved.
/// </param>
/// <param name="EfCoreVersion">
/// The <c>Microsoft.EntityFrameworkCore</c> version in effect. The dialect picker must not offer a provider
/// whose major version differs from this.
/// </param>
/// <param name="TargetFramework">The project's target framework moniker, e.g. <c>net10.0</c>.</param>
/// <param name="AdditionalProjectPaths">
/// Other projects the worker must also reference with a <c>#:project</c> directive, typically the project
/// that actually holds the DbContext when the query lives in a different one.
/// </param>
public sealed record ProjectContext(
    string ProjectPath,
    string ProjectName,
    EfProvider Provider,
    string? ProviderPackageVersion,
    string? EfCoreVersion,
    string? TargetFramework,
    IReadOnlyList<string> AdditionalProjectPaths)
{
    /// <summary>Every project path the worker must reference, starting with <see cref="ProjectPath"/>.</summary>
    public IReadOnlyList<string> AllProjectPaths
        => new[] { this.ProjectPath }.Concat(this.AdditionalProjectPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The major version of <see cref="EfCoreVersion"/>, or <see langword="null"/> when unknown.</summary>
    public int? EfCoreMajorVersion
    {
        get
        {
            if (string.IsNullOrEmpty(this.EfCoreVersion))
            {
                return null;
            }

            var separator = this.EfCoreVersion!.IndexOf('.');
            var head = separator < 0 ? this.EfCoreVersion : this.EfCoreVersion.Substring(0, separator);
            return int.TryParse(head, out var major) ? major : null;
        }
    }
}
