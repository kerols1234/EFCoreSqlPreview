namespace EFCoreSqlPreview.Core.Projects;

/// <summary>
/// The EF Core database provider a preview runs against.
/// </summary>
public enum EfProvider
{
    /// <summary>No provider could be detected.</summary>
    Unknown,

    /// <summary><c>Microsoft.EntityFrameworkCore.SqlServer</c>.</summary>
    SqlServer,

    /// <summary><c>Npgsql.EntityFrameworkCore.PostgreSQL</c>.</summary>
    PostgreSql,

    /// <summary><c>Microsoft.EntityFrameworkCore.Sqlite</c>.</summary>
    Sqlite,

    /// <summary><c>Pomelo.EntityFrameworkCore.MySql</c>.</summary>
    MySql,

    /// <summary><c>Oracle.EntityFrameworkCore</c>.</summary>
    Oracle,
}

/// <summary>
/// Everything the worker generator needs to target one provider.
/// </summary>
/// <param name="Provider">The provider this describes.</param>
/// <param name="DisplayName">The name shown in the dialect picker.</param>
/// <param name="PackageId">The NuGet package that supplies the provider.</param>
/// <param name="ConfigureCall">
/// The <c>DbContextOptionsBuilder</c> call, with <c>{0}</c> as the connection-string variable, e.g.
/// <c>UseSqlServer({0})</c>.
/// </param>
/// <param name="ConnectionString">
/// A connection string that is never actually contacted, because the interceptor suppresses connection opening.
/// </param>
public sealed record EfProviderDescriptor(
    EfProvider Provider,
    string DisplayName,
    string PackageId,
    string ConfigureCall,
    string ConnectionString);

/// <summary>
/// The provider catalog, verified end to end against all five supported providers.
/// </summary>
public static class EfProviders
{
    private static readonly EfProviderDescriptor[] Descriptors =
    [
        new(
            EfProvider.SqlServer,
            "SQL Server",
            "Microsoft.EntityFrameworkCore.SqlServer",
            "UseSqlServer({0})",
            "Server=.;Database=EFCoreSqlPreview;Trusted_Connection=True;TrustServerCertificate=True"),
        new(
            EfProvider.PostgreSql,
            "PostgreSQL",
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            "UseNpgsql({0})",
            "Host=localhost;Database=EFCoreSqlPreview;Username=preview;Password=preview"),
        new(
            EfProvider.Sqlite,
            "SQLite",
            "Microsoft.EntityFrameworkCore.Sqlite",
            "UseSqlite({0})",
            "Data Source=:memory:"),
        new(
            EfProvider.MySql,
            "MySQL (Pomelo)",
            "Pomelo.EntityFrameworkCore.MySql",
            "UseMySql({0}, new global::Microsoft.EntityFrameworkCore.MySqlServerVersion(new global::System.Version(8, 0, 32)))",
            "Server=localhost;Database=EFCoreSqlPreview;User=preview;Password=preview"),
        new(
            EfProvider.Oracle,
            "Oracle",
            "Oracle.EntityFrameworkCore",
            "UseOracle({0})",
            "User Id=preview;Password=preview;Data Source=localhost:1521/XE"),
    ];

    /// <summary>Every supported provider, in dialect-picker display order.</summary>
    public static IReadOnlyList<EfProviderDescriptor> All => Descriptors;

    /// <summary>Looks up a provider descriptor.</summary>
    /// <param name="provider">The provider to describe.</param>
    /// <returns>The descriptor, or <see langword="null"/> for <see cref="EfProvider.Unknown"/>.</returns>
    public static EfProviderDescriptor? Describe(EfProvider provider)
        => Descriptors.FirstOrDefault(d => d.Provider == provider);

    /// <summary>Maps a NuGet package id to the provider it supplies.</summary>
    /// <param name="packageId">The package id, compared case-insensitively.</param>
    /// <returns>The provider, or <see cref="EfProvider.Unknown"/> when the id is not a known provider.</returns>
    public static EfProvider FromPackageId(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return EfProvider.Unknown;
        }

        foreach (var descriptor in Descriptors)
        {
            if (string.Equals(descriptor.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor.Provider;
            }
        }

        return EfProvider.Unknown;
    }
}
