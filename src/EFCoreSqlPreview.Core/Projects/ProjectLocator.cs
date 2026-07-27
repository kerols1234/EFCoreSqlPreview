using System.Xml.Linq;
using EFCoreSqlPreview.Core.Infrastructure;

namespace EFCoreSqlPreview.Core.Projects;

/// <summary>
/// Finds the project that owns a source file.
/// </summary>
public interface IProjectLocator
{
    /// <summary>Walks up from a document to the nearest <c>.csproj</c>.</summary>
    /// <param name="documentPath">Absolute path to the source file.</param>
    /// <returns>The absolute project path, or <see langword="null"/> when none was found.</returns>
    string? FindOwningProject(string documentPath);

    /// <summary>Reads the <c>ProjectReference</c> graph rooted at a project.</summary>
    /// <param name="projectPath">Absolute path to a <c>.csproj</c>.</param>
    /// <returns>Absolute paths of every transitively referenced project, excluding the root.</returns>
    IReadOnlyList<string> GetTransitiveProjectReferences(string projectPath);
}

/// <summary>
/// The default <see cref="IProjectLocator"/>: plain file-system and XML inspection, no MSBuild evaluation.
/// </summary>
/// <remarks>
/// Loading MSBuild would be accurate but far too slow for a command that must feel instant. The worker's own
/// <c>dotnet run</c> does the real evaluation later.
/// </remarks>
public sealed class ProjectLocator : IProjectLocator
{

    // A pathological directory tree should degrade, not hang. Both limits are far above any real solution.
    private const int MaxAncestorsWalked = 64;
    private const int MaxProjectsVisited = 512;

    /// <summary>Creates a locator over the real file system.</summary>
    public ProjectLocator()
        : this(Infrastructure.FileSystem.Default)
    {
    }

    /// <summary>Creates a locator over a specific file system.</summary>
    /// <param name="fileSystem">Used for every path probe and read.</param>
    public ProjectLocator(IFileSystem fileSystem)
        => this.FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>The file system this locator reads through.</summary>
    public IFileSystem FileSystem { get; }

    /// <inheritdoc />
    public string? FindOwningProject(string documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        var full = this.FileSystem.GetFullPath(documentPath);
        var directory = this.FileSystem.DirectoryExists(full) ? full : this.FileSystem.GetDirectoryName(full);

        for (var depth = 0; depth < MaxAncestorsWalked && !string.IsNullOrEmpty(directory); depth++)
        {
            var projects = this.FileSystem.EnumerateFiles(directory!, "*.csproj");
            if (projects.Count > 0)
            {
                return ChooseProject(projects, directory!);
            }

            directory = this.FileSystem.GetDirectoryName(directory!);
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTransitiveProjectReferences(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return Array.Empty<string>();
        }

        var root = this.FileSystem.GetFullPath(projectPath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var ordered = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);

        while (queue.Count > 0 && seen.Count < MaxProjectsVisited)
        {
            var current = queue.Dequeue();
            foreach (var reference in this.ReadProjectReferences(current))
            {
                if (seen.Add(reference))
                {
                    ordered.Add(reference);
                    queue.Enqueue(reference);
                }
            }
        }

        return ordered;
    }

    /// <summary>
    /// Reads the direct <c>ProjectReference</c> items of one project, resolved to absolute paths.
    /// </summary>
    /// <param name="projectPath">Absolute path to a <c>.csproj</c>.</param>
    /// <returns>Absolute paths of the referenced projects that exist on disk.</returns>
    public IReadOnlyList<string> ReadProjectReferences(string projectPath)
    {
        var document = this.LoadProjectXml(projectPath);
        if (document?.Root is null)
        {
            return Array.Empty<string>();
        }

        var directory = this.FileSystem.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(directory))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        foreach (var element in document.Root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include) || include!.IndexOf('$') >= 0 || include.IndexOf('*') >= 0)
            {
                // Unevaluated MSBuild properties and globs cannot be resolved without loading MSBuild.
                continue;
            }

            var combined = CombinePath(directory!, include);
            var resolved = this.FileSystem.GetFullPath(combined);
            if (this.FileSystem.FileExists(resolved))
            {
                results.Add(resolved);
            }
        }

        return results;
    }

    /// <summary>
    /// Loads a project or props file as XML, tolerating anything that is not well formed.
    /// </summary>
    /// <param name="path">Absolute path to an MSBuild file.</param>
    /// <returns>The parsed document, or <see langword="null"/> when it could not be read or parsed.</returns>
    public XDocument? LoadProjectXml(string path)
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
            return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Combines an MSBuild-style relative path with a base directory, accepting either slash as separator.
    /// </summary>
    /// <param name="baseDirectory">The directory the relative path is written against.</param>
    /// <param name="relative">The relative path as written in the project file.</param>
    /// <returns>An unnormalised combined path.</returns>
    internal static string CombinePath(string baseDirectory, string relative)
    {
        var normalized = relative.Replace('\\', System.IO.Path.DirectorySeparatorChar)
                                 .Replace('/', System.IO.Path.DirectorySeparatorChar);
        return System.IO.Path.Combine(baseDirectory, normalized);
    }

    private static string ChooseProject(IReadOnlyList<string> projects, string directory)
    {
        if (projects.Count == 1)
        {
            return projects[0];
        }

        // Several projects in one folder is unusual; prefer the one named after the folder so the choice is
        // predictable rather than alphabetical luck.
        var folderName = new System.IO.DirectoryInfo(directory).Name;
        foreach (var project in projects)
        {
            if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(project), folderName, StringComparison.OrdinalIgnoreCase))
            {
                return project;
            }
        }

        return projects[0];
    }
}
