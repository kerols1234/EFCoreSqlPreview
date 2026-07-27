using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SampleShop;

/// <summary>
/// The design-time factory <c>dotnet ef</c> uses, and the first rung of the preview worker's activation ladder.
/// </summary>
/// <remarks>
/// The connection string points at a host that does not exist. Nothing ever dials it: the preview worker
/// suppresses connection opening, and migrations tooling would be pointed elsewhere.
/// </remarks>
public sealed class DesignTimeShopDbContextFactory : IDesignTimeDbContextFactory<ShopDbContext>
{
    /// <inheritdoc />
    public ShopDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ShopDbContext>()
            .UseSqlServer("Server=.;Database=SampleShop;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new ShopDbContext(options);
    }
}
