using System.Text.RegularExpressions;
using System.Xml.Linq;
using EFCoreSqlPreview.Core.Infrastructure;

namespace EFCoreSqlPreview.Core.Projects;

/// <summary>
/// Works out which EF Core provider a project uses.
/// </summary>
public interface IProviderDetector
{
    /// <summary>Builds the full project context for a document's owning project.</summary>
    /// <param name="projectPath">Absolute path to the <c>.csproj</c>.</param>
    /// <returns>
    /// The detected context. <see cref="ProjectContext.Provider"/> is <see cref="EfProvider.Unknown"/> when no
    /// provider package could be found anywhere in the reference graph.
    /// </returns>
    ProjectContext Detect(string projectPath);
}

/// <summary>
/// The packages one MSBuild project declares, after <c>Directory.Build.props</c> properties and central
/// package management have been folded in.
/// </summary>
/// <param name="ProjectPath">The project these packages belong to.</param>
/// <param name="Packages">Package id to resolved version; the version is <see langword="null"/> when unresolvable.</param>
/// <param name="TargetFramework">The first target framework the project declares.</param>
public sealed record ProjectPackages(
    string ProjectPath,
    IReadOnlyDictionary<string, string?> Packages,
    string? TargetFramework);

/// <summary>
/// The default <see cref="IProviderDetector"/>: inspects the project file, <c>Directory.Packages.props</c> and
/// <c>Directory.Build.props</c>, then follows <c>ProjectReference</c> chains when the provider is not local.
/// </summary>
public sealed class ProviderDetector : IProviderDetector
{
    /// <summary>Packages that identify the project holding the DbContext even when it holds no provider.</summary>
    private static readonly string[] EfCoreCorePackages =
    {
        "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Relational",
        "Microsoft.EntityFrameworkCore.Design",
    };

    private static readonly Regex PropertyReference = new(@"\$\(([A-Za-z_][A-Za-z0-9_]*)\)", RegexOptions.Compiled);

    private static readonly Regex SolutionProjectLine = new(
        "^Project\\(\"\\{[^}]*\\}\"\\)\\s*=\\s*\"[^\"]*\"\\s*,\\s*\"(?<path>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private const int MaxAncestorsWalked = 64;

    /// <summary>Creates a detector using the default <see cref="ProjectLocator"/>.</summary>
    public ProviderDetector()
        : this(new ProjectLocator())
    {
    }

    /// <summary>Creates a detector over a specific locator.</summary>
    /// <param name="locator">Used to follow project references.</param>
    public ProviderDetector(IProjectLocator locator)
        : this(locator, Infrastructure.FileSystem.Default)
    {
    }

    /// <summary>Creates a detector over a specific locator and file system.</summary>
    /// <param name="locator">Used to follow project references.</param>
    /// <param name="fileSystem">Used for every path probe and read.</param>
    public ProviderDetector(IProjectLocator locator, IFileSystem fileSystem)
    {
        this.Locator = locator ?? throw new ArgumentNullException(nameof(locator));
        this.FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>The locator used to follow project references.</summary>
    public IProjectLocator Locator { get; }

    /// <summary>The file system this detector reads through.</summary>
    public IFileSystem FileSystem { get; }

    /// <inheritdoc />
    public ProjectContext Detect(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("A project path is required.", nameof(projectPath));
        }

        var root = this.FileSystem.GetFullPath(projectPath);
        var rootPackages = this.ReadPackages(root);

        var closure = new List<string> { root };
        closure.AddRange(this.Locator.GetTransitiveProjectReferences(root));

        var candidates = new List<ProjectPackages>(closure.Count);
        foreach (var path in closure)
        {
            candidates.Add(string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ? rootPackages : this.ReadPackages(path));
        }

        var provider = EfProvider.Unknown;
        string? providerVersion = null;
        string? providerHolder = null;

        foreach (var candidate in candidates)
        {
            foreach (var package in candidate.Packages)
            {
                var detected = EfProviders.FromPackageId(package.Key);
                if (detected != EfProvider.Unknown)
                {
                    provider = detected;
                    providerVersion = package.Value;
                    providerHolder = candidate.ProjectPath;
                    break;
                }
            }

            if (provider != EfProvider.Unknown)
            {
                break;
            }
        }

        var additional = new List<string>();

        if (provider == EfProvider.Unknown)
        {
            // The provider may live in a project the document's project does not reference, for instance a
            // console host that wires up the context while the query sits in a class library.
            var sibling = this.FindProviderInSolution(root, closure);
            if (sibling is not null)
            {
                provider = sibling.Value.Provider;
                providerVersion = sibling.Value.Version;
                providerHolder = sibling.Value.ProjectPath;
                additional.Add(sibling.Value.ProjectPath);
            }
        }
        else if (providerHolder is not null
            && !string.Equals(providerHolder, root, StringComparison.OrdinalIgnoreCase)
            && !closure.Contains(providerHolder, StringComparer.OrdinalIgnoreCase))
        {
            additional.Add(providerHolder);
        }

        var efCoreVersion = FindEfCoreVersion(candidates) ?? providerVersion;

        return new ProjectContext(
            ProjectPath: root,
            ProjectName: System.IO.Path.GetFileNameWithoutExtension(root),
            Provider: provider,
            ProviderPackageVersion: providerVersion,
            EfCoreVersion: efCoreVersion,
            TargetFramework: rootPackages.TargetFramework,
            AdditionalProjectPaths: additional);
    }

    /// <summary>
    /// Reads one project's effective package list, folding in <c>Directory.Build.props</c> properties and
    /// <c>Directory.Packages.props</c> central versions.
    /// </summary>
    /// <param name="projectPath">Absolute path to a <c>.csproj</c>.</param>
    /// <returns>The resolved packages and target framework; empty when the file cannot be read.</returns>
    public ProjectPackages ReadPackages(string projectPath)
    {
        var full = this.FileSystem.GetFullPath(projectPath);
        var project = this.LoadXml(full);
        var properties = this.CollectProperties(full, project);
        var centralVersions = this.CollectCentralPackageVersions(full, properties);

        var packages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (project?.Root is not null)
        {
            foreach (var element in project.Root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                id = Expand(id!.Trim(), properties);

                var version = (string?)element.Attribute("VersionOverride")
                    ?? (string?)element.Attribute("Version")
                    ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

                version = version is null ? null : Expand(version.Trim(), properties);

                if (string.IsNullOrWhiteSpace(version) && centralVersions.TryGetValue(id, out var central))
                {
                    version = central;
                }

                packages[id] = NullIfUnresolved(version);
            }
        }

        // Central package management can also pin a package the project never names, but only referenced
        // packages matter here, so the central table is used purely to fill in missing versions.
        var targetFramework = ResolveTargetFramework(properties);
        return new ProjectPackages(full, packages, targetFramework);
    }

    private static string? FindEfCoreVersion(IReadOnlyList<ProjectPackages> candidates)
    {
        foreach (var name in EfCoreCorePackages)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Packages.TryGetValue(name, out var version) && !string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
        }

        return null;
    }

    private (EfProvider Provider, string? Version, string ProjectPath)? FindProviderInSolution(
        string projectPath,
        IReadOnlyCollection<string> alreadyChecked)
    {
        foreach (var solution in this.FindSolutions(projectPath))
        {
            foreach (var candidate in this.ReadSolutionProjects(solution))
            {
                if (alreadyChecked.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var packages = this.ReadPackages(candidate);
                foreach (var package in packages.Packages)
                {
                    var detected = EfProviders.FromPackageId(package.Key);
                    if (detected != EfProvider.Unknown)
                    {
                        return (detected, package.Value, candidate);
                    }
                }
            }
        }

        return null;
    }

    private IEnumerable<string> FindSolutions(string projectPath)
    {
        foreach (var directory in this.Ancestors(projectPath))
        {
            foreach (var pattern in new[] { "*.slnx", "*.sln" })
            {
                foreach (var file in this.FileSystem.EnumerateFiles(directory, pattern))
                {
                    yield return file;
                }
            }
        }
    }

    private IReadOnlyList<string> ReadSolutionProjects(string solutionPath)
    {
        var directory = this.FileSystem.GetDirectoryName(solutionPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Array.Empty<string>();
        }

        var text = this.FileSystem.ReadAllText(solutionPath);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var relatives = new List<string>();

        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var document = XDocument.Parse(text);
                foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "Project"))
                {
                    var path = (string?)element.Attribute("Path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        relatives.Add(path!);
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                return Array.Empty<string>();
            }
        }
        else
        {
            foreach (Match match in SolutionProjectLine.Matches(text))
            {
                relatives.Add(match.Groups["path"].Value);
            }
        }

        var results = new List<string>();
        foreach (var relative in relatives)
        {
            if (!relative.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var resolved = this.FileSystem.GetFullPath(ProjectLocator.CombinePath(directory!, relative));
            if (this.FileSystem.FileExists(resolved))
            {
                results.Add(resolved);
            }
        }

        return results;
    }

    private Dictionary<string, string> CollectProperties(string projectPath, XDocument? project)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Outermost first so a nearer file wins, matching MSBuild's import order closely enough for versions.
        foreach (var propsFile in this.FindAncestorFiles(projectPath, "Directory.Build.props").Reverse())
        {
            AddProperties(properties, this.LoadXml(propsFile));
        }

        AddProperties(properties, project);
        return properties;
    }

    private Dictionary<string, string?> CollectCentralPackageVersions(
        string projectPath,
        IReadOnlyDictionary<string, string> properties)
    {
        var versions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var propsFile in this.FindAncestorFiles(projectPath, "Directory.Packages.props").Reverse())
        {
            var document = this.LoadXml(propsFile);
            if (document?.Root is null)
            {
                continue;
            }

            foreach (var element in document.Root.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
            {
                var id = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update");
                var version = (string?)element.Attribute("Version")
                    ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

                if (!string.IsNullOrWhiteSpace(id))
                {
                    versions[Expand(id!.Trim(), properties)] = NullIfUnresolved(
                        version is null ? null : Expand(version.Trim(), properties));
                }
            }
        }

        return versions;
    }

    private IReadOnlyList<string> FindAncestorFiles(string startPath, string fileName)
    {
        var results = new List<string>();
        foreach (var directory in this.Ancestors(startPath))
        {
            var candidate = System.IO.Path.Combine(directory, fileName);
            if (this.FileSystem.FileExists(candidate))
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    private IEnumerable<string> Ancestors(string startPath)
    {
        var full = this.FileSystem.GetFullPath(startPath);
        var directory = this.FileSystem.DirectoryExists(full) ? full : this.FileSystem.GetDirectoryName(full);

        for (var depth = 0; depth < MaxAncestorsWalked && !string.IsNullOrEmpty(directory); depth++)
        {
            yield return directory!;
            directory = this.FileSystem.GetDirectoryName(directory!);
        }
    }

    private XDocument? LoadXml(string path)
    {
        if (!this.FileSystem.FileExists(path))
        {
            return null;
        }

        var text = this.FileSystem.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static void AddProperties(Dictionary<string, string> properties, XDocument? document)
    {
        if (document?.Root is null)
        {
            return;
        }

        foreach (var group in document.Root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            foreach (var property in group.Elements())
            {
                properties[property.Name.LocalName] = property.Value.Trim();
            }
        }
    }

    private static string? ResolveTargetFramework(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.TryGetValue("TargetFramework", out var single) && !string.IsNullOrWhiteSpace(single))
        {
            var expanded = Expand(single, properties);
            return expanded.IndexOf('$') >= 0 ? null : expanded;
        }

        if (properties.TryGetValue("TargetFrameworks", out var many) && !string.IsNullOrWhiteSpace(many))
        {
            var expanded = Expand(many, properties);
            var first = expanded.Split(';').FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
            return string.IsNullOrEmpty(first) || first!.IndexOf('$') >= 0 ? null : first;
        }

        return null;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> properties)
    {
        if (value.IndexOf('$') < 0)
        {
            return value;
        }

        // One pass covers the realistic case ($(EfCoreVersion) defined in Directory.Build.props); chasing
        // deeper indirection would need real MSBuild evaluation.
        return PropertyReference.Replace(value, match =>
            properties.TryGetValue(match.Groups[1].Value, out var resolved) ? resolved : match.Value);
    }

    private static string? NullIfUnresolved(string? version)
        => string.IsNullOrWhiteSpace(version) || version!.IndexOf('$') >= 0 ? null : version;
}
