using System.Diagnostics;
using System.IO;

namespace EFCoreSqlPreview.Core.Tests.EndToEnd;

/// <summary>
/// Discovers the repository layout and decides whether a real end-to-end run is possible on this machine.
/// </summary>
/// <remarks>
/// The end-to-end tests really do shell out to <c>dotnet run --file</c> against <c>samples/SampleShop</c>. On a
/// machine with no .NET 10 SDK they cannot work at all, and a red test there would say nothing about the code,
/// so they skip with the reason instead.
/// </remarks>
public static class EndToEndEnvironment
{
    private static readonly Lazy<string?> RepositoryRootLazy = new(FindRepositoryRoot, isThreadSafe: true);
    private static readonly Lazy<string?> SkipReasonLazy = new(ComputeSkipReason, isThreadSafe: true);

    /// <summary>The repository root, or <see langword="null"/> when it could not be found.</summary>
    public static string? RepositoryRoot => RepositoryRootLazy.Value;

    /// <summary>The sample project the end-to-end queries run against.</summary>
    public static string? SampleShopProject => RepositoryRoot is null
        ? null
        : Path.Combine(RepositoryRoot, "samples", "SampleShop", "SampleShop.csproj");

    /// <summary>
    /// A virtual document path inside the sample project. The file never exists: the pipeline only needs the
    /// path to find the owning <c>.csproj</c>, and inventing it keeps the sample project untouched.
    /// </summary>
    public static string? SampleDocumentPath => RepositoryRoot is null
        ? null
        : Path.Combine(RepositoryRoot, "samples", "SampleShop", "PreviewEndToEndQueries.cs");

    /// <summary>Why the end-to-end tests cannot run here, or <see langword="null"/> when they can.</summary>
    public static string? SkipReason => SkipReasonLazy.Value;

    private static string? ComputeSkipReason()
    {
        if (RepositoryRoot is null)
        {
            return "The repository root could not be located from the test assembly, so samples/SampleShop is unreachable.";
        }

        if (!File.Exists(SampleShopProject!))
        {
            return $"The sample project '{SampleShopProject}' is missing.";
        }

        var sdks = RunDotnet("--list-sdks");
        if (sdks is null)
        {
            return "The 'dotnet' CLI could not be launched, so no worker could be built.";
        }

        var hasNet10 = sdks
            .Split('\n')
            .Select(line => line.Trim())
            .Any(line => line.StartsWith("10.", StringComparison.Ordinal));

        return hasNet10
            ? null
            : "No .NET 10 SDK is installed. The worker is a file-based app (dotnet run --file), which needs one.\nInstalled SDKs:\n" + sdks.Trim();
    }

    private static string? FindRepositoryRoot()
        => WalkUp(AppContext.BaseDirectory) ?? WalkUp(Path.GetDirectoryName(ThisFilePath()) ?? string.Empty);

    private static string? WalkUp(string start)
    {
        if (string.IsNullOrEmpty(start))
        {
            return null;
        }

        var directory = new DirectoryInfo(start);

        for (var depth = 0; depth < 12 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EFCoreSqlPreview.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// The compile-time location of this file, used as a second anchor for the repository root.
    /// </summary>
    /// <param name="path">Supplied by the compiler; never pass it.</param>
    /// <returns>The absolute path of this source file as it was compiled.</returns>
    /// <remarks>
    /// The output directory is the natural anchor, but it is only inside the repository when the tests run
    /// from where they were built. This keeps the tests working from a copied output tree too.
    /// </remarks>
    private static string ThisFilePath([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => path;

    private static string? RunDotnet(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            return output;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
