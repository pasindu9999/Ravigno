namespace StockTransfer.Api.Models;

// Goods sent but not received (damaged, missing). Written off: neither location holds them.
public class ReceiveDiscrepancy
{
    public int Id { get; set; }
    public int TransferId { get; set; }
    public int ProductId { get; set; }
    public int MissingQuantity { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }

    public TransferLine TransferLine { get; set; } = null!;
}
