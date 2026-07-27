using System.IO;

namespace EFCoreSqlPreview.Core.Tests.Fakes;

/// <summary>
/// A throwaway directory tree for tests that exercise the real file system.
/// </summary>
/// <remarks>
/// <see cref="Core.Projects.ProjectLocator"/> and <see cref="Core.Projects.ProviderDetector"/> are almost
/// entirely path arithmetic, so faking the file system underneath them would mostly test the fake. A real
/// temp tree keeps <c>Path.GetFullPath</c>, <c>DirectoryInfo.Name</c> and ancestor walking honest.
/// </remarks>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>Creates an empty tree under the system temp directory.</summary>
    public TempWorkspace()
    {
        this.Root = Path.Combine(Path.GetTempPath(), "efcoresqlpreview-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(this.Root);
    }

    /// <summary>The absolute root of the tree.</summary>
    public string Root { get; }

    /// <summary>Combines path segments onto the root without creating anything.</summary>
    /// <param name="segments">Relative segments.</param>
    /// <returns>The absolute path.</returns>
    public string At(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { this.Root }.Concat(segments).ToArray()));

    /// <summary>Writes a file, creating its directory.</summary>
    /// <param name="relativePath">Path relative to <see cref="Root"/>, using forward or back slashes.</param>
    /// <param name="contents">The file text.</param>
    /// <returns>The absolute path written.</returns>
    public string Write(string relativePath, string contents)
    {
        var full = Path.GetFullPath(Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>Creates a directory.</summary>
    /// <param name="relativePath">Path relative to <see cref="Root"/>.</param>
    /// <returns>The absolute path created.</returns>
    public string Directory_(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp tree is harmless; failing the test over it is not.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
