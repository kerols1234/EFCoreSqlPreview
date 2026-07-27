namespace SampleShop;

/// <summary>A flattened <see cref="Product"/>, used to prove DTO projections resolve.</summary>
public class ProductDto
{
    /// <summary>The product id.</summary>
    public int Id { get; set; }

    /// <summary>The product name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The unit price.</summary>
    public decimal Price { get; set; }

    /// <summary>The category name, pulled across the navigation so the SQL contains a join.</summary>
    public string CategoryName { get; set; } = string.Empty;
}

/// <summary>A rolled-up <see cref="Order"/>, used to prove aggregate projections resolve.</summary>
public class OrderSummaryDto
{
    /// <summary>The order id.</summary>
    public int OrderId { get; set; }

    /// <summary>The customer who placed it.</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>When it was placed.</summary>
    public DateTime PlacedOn { get; set; }

    /// <summary>How many lines it has.</summary>
    public int LineCount { get; set; }

    /// <summary>The order total.</summary>
    public decimal Total { get; set; }
}
