namespace SampleShop;

/// <summary>
/// Custom queryable operators. These exist to prove that user-defined extension methods inside a previewed
/// query resolve correctly, because the generated worker is compiled against this project by the real compiler.
/// </summary>
public static class ShopQueryableExtensions
{
    /// <summary>Restricts to products that are currently sellable.</summary>
    /// <param name="source">The product query.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<Product> ActiveOnly(this IQueryable<Product> source)
        => source.Where(p => p.IsActive);

    /// <summary>Restricts to products whose name or SKU contains a term.</summary>
    /// <param name="source">The product query.</param>
    /// <param name="term">The search term. An empty term matches everything.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<Product> Search(this IQueryable<Product> source, string term)
        => string.IsNullOrWhiteSpace(term)
            ? source
            : source.Where(p => p.Name.Contains(term) || p.Sku.Contains(term));

    /// <summary>Restricts to products priced above a threshold.</summary>
    /// <param name="source">The product query.</param>
    /// <param name="min">The exclusive lower bound.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<Product> PricedAbove(this IQueryable<Product> source, decimal min)
        => source.Where(p => p.Price > min);

    /// <summary>Projects products onto <see cref="ProductDto"/>, pulling the category name across the navigation.</summary>
    /// <param name="source">The product query.</param>
    /// <returns>The projected query.</returns>
    public static IQueryable<ProductDto> ToDto(this IQueryable<Product> source)
        => source.Select(p => new ProductDto
        {
            Id = p.Id,
            Name = p.Name,
            Price = p.Price,
            CategoryName = p.Category!.Name,
        });
}
