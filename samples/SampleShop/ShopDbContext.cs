using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace SampleShop;

/// <summary>
/// The sample shop model. Has the plain <c>DbContextOptions&lt;T&gt;</c> constructor, which is the second rung
/// of the worker's activation ladder.
/// </summary>
/// <param name="options">Configured options, supplied by the host or by the preview worker.</param>
public class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    /// <summary>Product catalogue.</summary>
    public DbSet<Product> Products => this.Set<Product>();

    /// <summary>Product categories.</summary>
    public DbSet<Category> Categories => this.Set<Category>();

    /// <summary>Customers.</summary>
    public DbSet<Customer> Customers => this.Set<Customer>();

    /// <summary>Orders.</summary>
    public DbSet<Order> Orders => this.Set<Order>();

    /// <summary>Order lines.</summary>
    public DbSet<OrderLine> OrderLines => this.Set<OrderLine>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ShopModel.Configure(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>
/// A second sample context whose constructor also takes a service. This is what exercises the worker's
/// DI-constructor rung, where non-options parameters have to be stubbed.
/// </summary>
/// <param name="options">Configured options.</param>
/// <param name="clock">An injected collaborator the preview worker has to fabricate.</param>
public class DiShopDbContext(DbContextOptions<DiShopDbContext> options, IShopClock clock) : DbContext(options)
{
    private readonly IShopClock clock = clock;

    /// <summary>Product catalogue.</summary>
    public DbSet<Product> Products => this.Set<Product>();

    /// <summary>Orders.</summary>
    public DbSet<Order> Orders => this.Set<Order>();

    /// <summary>Orders placed today, according to the injected clock.</summary>
    /// <returns>A deferred query filtered on the clock's current date.</returns>
    public IQueryable<Order> OrdersPlacedToday()
        => this.Orders.Where(o => o.PlacedOn >= this.clock.UtcNow.Date);

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ShopModel.Configure(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>A trivial ambient-time abstraction, present only so a context constructor has something to inject.</summary>
public interface IShopClock
{
    /// <summary>The current UTC time.</summary>
    DateTime UtcNow { get; }
}

/// <summary>The real <see cref="IShopClock"/>.</summary>
public sealed class SystemShopClock : IShopClock
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// The model configuration, shared by both sample contexts.
/// </summary>
internal static class ShopModel
{
    /// <summary>Configures entities, navigations, the owned address and the tag value converter.</summary>
    /// <param name="modelBuilder">The builder to configure.</param>
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(128).IsRequired();
            entity.HasMany(c => c.Products)
                  .WithOne(p => p.Category!)
                  .HasForeignKey(p => p.CategoryId);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.Property(p => p.Name).HasMaxLength(256).IsRequired();
            entity.Property(p => p.Sku).HasMaxLength(64).IsRequired();
            entity.Property(p => p.Price).HasPrecision(18, 2);

            // A value converter rather than an owned JSON column: JSON-mapped owned entities cannot
            // materialize the preview worker's synthetic row, and this stays previewable.
            entity.Property(p => p.Tags)
                  .HasConversion(
                      tags => string.Join("|", tags),
                      value => value.Length == 0
                          ? new List<string>()
                          : value.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(),
                      new ValueComparer<List<string>>(
                          (left, right) => left != null && right != null && left.SequenceEqual(right),
                          tags => tags.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode())),
                          tags => tags.ToList()))
                  .HasMaxLength(512);

            entity.HasIndex(p => p.Sku).IsUnique();
        });

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(c => c.Name).HasMaxLength(256).IsRequired();
            entity.Property(c => c.Email).HasMaxLength(256).IsRequired();

            entity.OwnsOne(c => c.BillingAddress, address =>
            {
                address.Property(a => a.Street).HasMaxLength(256).HasColumnName("BillingStreet");
                address.Property(a => a.City).HasMaxLength(128).HasColumnName("BillingCity");
                address.Property(a => a.PostalCode).HasMaxLength(32).HasColumnName("BillingPostalCode");
                address.Property(a => a.Country).HasMaxLength(64).HasColumnName("BillingCountry");
            });

            entity.HasMany(c => c.Orders)
                  .WithOne(o => o.Customer!)
                  .HasForeignKey(o => o.CustomerId);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasMany(o => o.Lines)
                  .WithOne(l => l.Order!)
                  .HasForeignKey(l => l.OrderId);
        });

        modelBuilder.Entity<OrderLine>(entity =>
        {
            entity.Property(l => l.UnitPrice).HasPrecision(18, 2);
            entity.HasOne(l => l.Product)
                  .WithMany(p => p.OrderLines)
                  .HasForeignKey(l => l.ProductId);
        });
    }
}
