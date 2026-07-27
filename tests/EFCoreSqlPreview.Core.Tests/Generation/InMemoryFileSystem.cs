using System.IO;
using EFCoreSqlPreview.Core.Infrastructure;

namespace EFCoreSqlPreview.Core.Tests.Fakes;

/// <summary>
/// An <see cref="IFileSystem"/> that never touches a disk, so generator tests neither write into the real
/// <c>%LOCALAPPDATA%</c> nor depend on what is already there.
/// </summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file written, keyed by absolute path.</summary>
    public IReadOnlyDictionary<string, string> Files => this.files;

    /// <summary>How many times <see cref="WriteAllText"/> actually changed a file.</summary>
    public int EffectiveWrites { get; private set; }

    /// <inheritdoc />
    public string LocalApplicationDataPath { get; set; } = @"C:\fake\LocalAppData";

    /// <summary>Seeds a file without counting it as a write.</summary>
    /// <param name="path">Absolute path.</param>
    /// <param name="contents">The file text.</param>
    public void Seed(string path, string contents) => this.files[path] = contents;

    /// <inheritdoc />
    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && this.files.ContainsKey(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && this.directories.Contains(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => this.files.TryGetValue(path, out var value) ? value : string.Empty;

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        // Mirrors the real implementation: an identical write is skipped so the SDK's artifact cache stays warm.
        if (this.files.TryGetValue(path, out var existing) && string.Equals(existing, contents, StringComparison.Ordinal))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            this.directories.Add(directory!);
        }

        this.files[path] = contents;
        this.EffectiveWrites++;
    }

    /// <inheritdoc />
    public void CreateDirectory(string path) => this.directories.Add(path);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern)
    {
        var suffix = searchPattern.StartsWith("*", StringComparison.Ordinal) ? searchPattern.Substring(1) : searchPattern;
        return this.files.Keys
            .Where(p => string.Equals(Path.GetDirectoryName(p), directory, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public string? GetDirectoryName(string path) => Path.GetDirectoryName(path);

    /// <inheritdoc />
    public string GetFullPath(string path) => path;
}
