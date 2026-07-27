using System.IO;
using System.Text;

namespace EFCoreSqlPreview.Core.Infrastructure;

/// <summary>
/// The file-system operations Core needs, behind an interface so tests never touch a real disk.
/// </summary>
public interface IFileSystem
{
    /// <summary>The user's local application data directory, where the scratch tree lives.</summary>
    string LocalApplicationDataPath { get; }

    /// <summary>Tests whether a file exists.</summary>
    /// <param name="path">Absolute path to test.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Tests whether a directory exists.</summary>
    /// <param name="path">Absolute path to test.</param>
    /// <returns><see langword="true"/> when the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Reads a whole file as text.</summary>
    /// <param name="path">Absolute path to read.</param>
    /// <returns>The file's contents, or an empty string when it cannot be read.</returns>
    string ReadAllText(string path);

    /// <summary>
    /// Writes a file, creating its directory first, and leaves the file untouched when the contents already match.
    /// </summary>
    /// <param name="path">Absolute path to write.</param>
    /// <param name="contents">The text to write, encoded as UTF-8 with a byte-order mark.</param>
    /// <remarks>
    /// Skipping an identical write preserves the timestamp, which is what lets the SDK reuse its build artifacts
    /// and turns a re-run from a four-second cold build into a two-and-a-half-second warm one.
    /// </remarks>
    void WriteAllText(string path, string contents);

    /// <summary>Creates a directory and every missing parent.</summary>
    /// <param name="path">Absolute directory path.</param>
    void CreateDirectory(string path);

    /// <summary>Lists the files in a directory that match a pattern, without recursing.</summary>
    /// <param name="directory">Absolute directory path.</param>
    /// <param name="searchPattern">A file glob such as <c>*.csproj</c>.</param>
    /// <returns>Absolute file paths, sorted ordinally; empty when the directory does not exist.</returns>
    IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern);

    /// <summary>Returns the directory portion of a path.</summary>
    /// <param name="path">A file or directory path.</param>
    /// <returns>The parent directory, or <see langword="null"/> at the root.</returns>
    string? GetDirectoryName(string path);

    /// <summary>Resolves a path to an absolute, canonical form.</summary>
    /// <param name="path">A relative or absolute path.</param>
    /// <returns>The absolute path; the input unchanged when it cannot be resolved.</returns>
    string GetFullPath(string path);
}

/// <summary>
/// The real <see cref="IFileSystem"/>, backed by <see cref="System.IO"/>.
/// </summary>
public sealed class FileSystem : IFileSystem
{
    // The C# compiler only reliably detects UTF-8 source from a byte-order mark, and the query text can
    // contain any identifier the user's language allows.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The shared instance.</summary>
    public static FileSystem Default { get; } = new();

    /// <inheritdoc />
    public string LocalApplicationDataPath
        => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <inheritdoc />
    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <inheritdoc />
    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <inheritdoc />
    public void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        if (File.Exists(path) && string.Equals(File.ReadAllText(path), contents, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(path, contents, Utf8WithBom);
    }

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateFiles(string directory, string searchPattern)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<string>();
        }

        try
        {
            var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files;
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public string? GetDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
        catch (NotSupportedException)
        {
            return path;
        }
    }
}
