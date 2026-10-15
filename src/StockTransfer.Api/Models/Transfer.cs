namespace StockTransfer.Api.Models;

public class Transfer
{
    public int Id { get; set; }
    public int FromLocationId { get; set; }
    public int ToLocationId { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Requested;
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    public Location FromLocation { get; set; } = null!;
    public Location ToLocation { get; set; } = null!;
    public List<TransferLine> Lines { get; set; } = [];
}
