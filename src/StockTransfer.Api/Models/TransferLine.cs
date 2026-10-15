namespace StockTransfer.Api.Models;

public class TransferLine
{
    public int TransferId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }

    public Transfer Transfer { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
