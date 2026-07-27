namespace SampleShop;

/// <summary>A product grouping. Reachable from <see cref="Product.Category"/> and back through <see cref="Products"/>.</summary>
public class Category
{
    /// <summary>Primary key.</summary>
    public int Id { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Products in this category.</summary>
    public List<Product> Products { get; set; } = [];
}

/// <summary>Something that can be sold.</summary>
public class Product
{
    /// <summary>Primary key.</summary>
    public int Id { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Stock-keeping unit.</summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>Unit price.</summary>
    public decimal Price { get; set; }

    /// <summary>Whether the product is currently sellable.</summary>
    public bool IsActive { get; set; }

    /// <summary>Free-form tags, stored as a single delimited column through a value converter.</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>Foreign key to <see cref="Category"/>.</summary>
    public int CategoryId { get; set; }

    /// <summary>The owning category.</summary>
    public Category? Category { get; set; }

    /// <summary>Order lines that reference this product.</summary>
    public List<OrderLine> OrderLines { get; set; } = [];
}

/// <summary>A postal address, mapped as an owned type so it table-splits into the owner.</summary>
public class Address
{
    /// <summary>Street line.</summary>
    public string Street { get; set; } = string.Empty;

    /// <summary>City.</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>Postal or ZIP code.</summary>
    public string PostalCode { get; set; } = string.Empty;

    /// <summary>ISO country name.</summary>
    public string Country { get; set; } = string.Empty;
}

/// <summary>Someone who places orders.</summary>
public class Customer
{
    /// <summary>Primary key.</summary>
    public int Id { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Contact email.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Owned address; produces extra columns on the customer table.</summary>
    public Address BillingAddress { get; set; } = new();

    /// <summary>Orders this customer placed.</summary>
    public List<Order> Orders { get; set; } = [];
}

/// <summary>Lifecycle state of an <see cref="Order"/>.</summary>
public enum OrderStatus
{
    /// <summary>Not yet submitted.</summary>
    Draft = 0,

    /// <summary>Submitted and awaiting fulfilment.</summary>
    Placed = 1,

    /// <summary>Sent to the customer.</summary>
    Shipped = 2,

    /// <summary>Cancelled before fulfilment.</summary>
    Cancelled = 3,
}

/// <summary>A customer's order.</summary>
public class Order
{
    /// <summary>Primary key.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key to <see cref="Customer"/>.</summary>
    public int CustomerId { get; set; }

    /// <summary>The customer who placed it.</summary>
    public Customer? Customer { get; set; }

    /// <summary>When the order was placed.</summary>
    public DateTime PlacedOn { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public OrderStatus Status { get; set; }

    /// <summary>The order's lines.</summary>
    public List<OrderLine> Lines { get; set; } = [];
}

/// <summary>One product on one order.</summary>
public class OrderLine
{
    /// <summary>Primary key.</summary>
    public int Id { get; set; }

    /// <summary>Foreign key to <see cref="Order"/>.</summary>
    public int OrderId { get; set; }

    /// <summary>The owning order.</summary>
    public Order? Order { get; set; }

    /// <summary>Foreign key to <see cref="Product"/>.</summary>
    public int ProductId { get; set; }

    /// <summary>The product being ordered.</summary>
    public Product? Product { get; set; }

    /// <summary>How many units.</summary>
    public int Quantity { get; set; }

    /// <summary>Price per unit at the time of ordering.</summary>
    public decimal UnitPrice { get; set; }
}
