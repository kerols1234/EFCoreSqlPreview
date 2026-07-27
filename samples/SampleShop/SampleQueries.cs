using Microsoft.EntityFrameworkCore;

namespace SampleShop;

/// <summary>
/// Realistic queries to select in the editor when trying the extension out. Each one exercises a different
/// part of the analyzer: custom extension methods, free variables, DTO projections, terminal operators,
/// dictionaries, split queries and query syntax.
/// </summary>
/// <param name="db">The shop context.</param>
public class SampleQueries(ShopDbContext db)
{
    private const int DefaultPageSize = 25;

    private readonly ShopDbContext db = db;

    /// <summary>Custom extension methods plus a DTO projection and a free variable.</summary>
    /// <param name="term">Search term, a parameter with no initializer.</param>
    /// <returns>Matching products.</returns>
    public async Task<List<ProductDto>> SearchProductsAsync(string term)
    {
        decimal minPrice = 100m;

        return await this.db.Products
            .ActiveOnly()
            .Search(term)
            .PricedAbove(minPrice)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                Price = p.Price,
                CategoryName = p.Category!.Name,
            })
            .Take(DefaultPageSize)
            .ToListAsync();
    }

    /// <summary>An aggregate terminal, which throws during materialization against an empty reader.</summary>
    /// <returns>The number of active products.</returns>
    public Task<int> CountActiveAsync()
        => this.db.Products.ActiveOnly().CountAsync();

    /// <summary>A dictionary terminal whose key and element selectors define the result shape.</summary>
    /// <param name="ids">Product ids, which EF expands into an IN list.</param>
    /// <returns>Names keyed by id.</returns>
    public Task<Dictionary<int, string>> NamesByIdAsync(int[] ids)
        => this.db.Products
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

    /// <summary>Include plus AsSplitQuery, which makes EF Core issue more than one command.</summary>
    /// <param name="customerId">The customer whose orders to load.</param>
    /// <returns>The customer's orders with their lines and products.</returns>
    public Task<List<Order>> OrdersWithLinesAsync(int customerId)
        => this.db.Orders
            .Where(o => o.CustomerId == customerId)
            .Include(o => o.Lines)
                .ThenInclude(l => l.Product)
            .AsSplitQuery()
            .ToListAsync();

    /// <summary>A grouped aggregate projected onto a DTO.</summary>
    /// <param name="since">Only orders placed on or after this date.</param>
    /// <returns>One summary per order.</returns>
    public Task<List<OrderSummaryDto>> SummariesAsync(DateTime since)
        => this.db.Orders
            .Where(o => o.PlacedOn >= since && o.Status != OrderStatus.Cancelled)
            .Select(o => new OrderSummaryDto
            {
                OrderId = o.Id,
                CustomerName = o.Customer!.Name,
                PlacedOn = o.PlacedOn,
                LineCount = o.Lines.Count,
                Total = o.Lines.Sum(l => l.Quantity * l.UnitPrice),
            })
            .ToListAsync();

    /// <summary>The query-builder shape: a local mutated across several statements before the terminal runs.</summary>
    /// <param name="onlyActive">Whether to add the active filter.</param>
    /// <param name="term">Optional search term.</param>
    /// <returns>Matching products.</returns>
    public async Task<List<Product>> BuildIncrementallyAsync(bool onlyActive, string? term)
    {
        var query = this.db.Products.AsQueryable();

        if (onlyActive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(p => p.Name.Contains(term));
        }

        return await query.OrderBy(p => p.Name).ToListAsync();
    }

    /// <summary>Query syntax with an anonymous projection and no terminal operator.</summary>
    /// <returns>A deferred query the analyzer must add a terminal to.</returns>
    public IQueryable<object> ActiveNamesQuerySyntax()
        => from p in this.db.Products
           where p.IsActive
           orderby p.Name
           select new { p.Id, p.Name, Category = p.Category!.Name };
}
