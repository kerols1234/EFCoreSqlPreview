using Microsoft.CodeAnalysis.Text;

namespace EFCoreSqlPreview.Core.Tests.EndToEnd;

/// <summary>
/// The virtual document the end-to-end tests analyse, plus the selection each scenario makes in it.
/// </summary>
/// <remarks>
/// The document is never written to disk. It only has to look like a file inside <c>samples/SampleShop</c>, so
/// the pipeline locates that project, and the worker is then compiled against it by the real C# compiler. That
/// is what makes <c>ProductDto</c>, <c>ShopDbContext</c> and the custom <c>IQueryable</c> extensions resolve.
/// </remarks>
public static class SampleShopQueries
{
    /// <summary>The document text handed to the analyzer.</summary>
    public const string Document = """
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace SampleShop;

/// <summary>Queries the end-to-end tests select from.</summary>
public class PreviewEndToEndQueries
{
    private readonly ShopDbContext db;

    public PreviewEndToEndQueries(ShopDbContext db) => this.db = db;

    // SCENARIO: dto-projection
    public async Task<List<ProductDto>> DtoProjectionAsync()
    {
        decimal minPrice = 100m;

        return await this.db.Products
            .Where(p => p.Price > minPrice)
            .Select(p => new ProductDto
            {
                Id = p.Id,
                Name = p.Name,
                Price = p.Price,
                CategoryName = p.Category!.Name,
            })
            .ToListAsync();
    }

    // SCENARIO: first-or-default
    public Task<Product?> SingleProductAsync()
        => this.db.Products.FirstOrDefaultAsync(p => p.Id == 42);

    // SCENARIO: count
    public Task<int> ActiveCountAsync()
        => this.db.Products.Where(p => p.IsActive).CountAsync();

    // SCENARIO: dictionary
    public Task<Dictionary<int, string>> NamesByIdAsync()
    {
        int[] ids = new[] { 1, 2, 3 };

        return this.db.Products
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);
    }

    // SCENARIO: custom-extensions
    public Task<List<ProductDto>> CustomExtensionsAsync()
    {
        string term = "widget";
        decimal floor = 10m;

        return this.db.Products
            .ActiveOnly()
            .Search(term)
            .PricedAbove(floor)
            .ToDto()
            .ToListAsync();
    }

    // SCENARIO: include
    public Task<List<Order>> OrdersWithLinesAsync()
    {
        int customerId = 7;

        return this.db.Orders
            .Where(o => o.CustomerId == customerId)
            .Include(o => o.Lines)
                .ThenInclude(l => l.Product)
            .ToListAsync();
    }

    // SCENARIO: split-query
    public Task<List<Order>> OrdersWithLinesSplitAsync()
        => this.db.Orders
            .Include(o => o.Lines)
                .ThenInclude(l => l.Product)
            .AsSplitQuery()
            .ToListAsync();
}
""";

    /// <summary>Finds the selection for one scenario.</summary>
    /// <param name="scenario">The name in the scenario marker comment.</param>
    /// <param name="startsAt">The first text of the query, searched for after the marker.</param>
    /// <param name="endsAfter">The last text of the query, searched for after the start.</param>
    /// <returns>The span to select.</returns>
    public static TextSpan Selection(string scenario, string startsAt, string endsAfter)
    {
        var marker = Document.IndexOf("// SCENARIO: " + scenario, StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new ArgumentException($"No scenario marker for '{scenario}'.", nameof(scenario));
        }

        var start = Document.IndexOf(startsAt, marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new ArgumentException($"'{startsAt}' does not occur after the '{scenario}' marker.", nameof(startsAt));
        }

        var end = Document.IndexOf(endsAfter, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new ArgumentException($"'{endsAfter}' does not occur after the '{scenario}' selection start.", nameof(endsAfter));
        }

        return TextSpan.FromBounds(start, end + endsAfter.Length);
    }
}
