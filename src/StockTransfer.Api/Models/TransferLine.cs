namespace StockTransfer.Api.Models;

public class TransferLine
{
    public int TransferId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    // Null until the transfer is received; then 0..Quantity.
    public int? ReceivedQuantity { get; set; }

    public Transfer Transfer { get; set; } = null!;
    public Product Product { get; set; } = null!;
}
