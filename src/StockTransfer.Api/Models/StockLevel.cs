namespace StockTransfer.Api.Models;

public class StockLevel
{
    public int LocationId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }

    public Location Location { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
