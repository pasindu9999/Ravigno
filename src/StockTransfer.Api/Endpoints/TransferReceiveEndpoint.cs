using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

public static class TransferReceiveEndpoint
{
    public static IEndpointRouteBuilder MapTransferReceive(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers/{id:int}/receive", ReceiveAsync);
        return app;
    }

    private static async Task<IResult> ReceiveAsync(int id, AppDbContext db, ILogger<Program> logger)
    {
        // Disposing without a commit rolls back, so any exception below also undoes the status change.
        await using var tx = await db.Database.BeginTransactionAsync();

        // The guard: only one caller can flip Dispatched -> Received. A concurrent second caller
        // blocks on this row lock until the first commits, then matches 0 rows.
        var now = DateTime.UtcNow;
        var flipped = await db.Transfers
            .Where(t => t.Id == id && t.Status == TransferStatus.Dispatched)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TransferStatus.Received)
                .SetProperty(t => t.ReceivedAt, now));

        if (flipped == 0)
        {
            await tx.RollbackAsync();
            var current = await LoadDtoAsync(db, id);
            if (current is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"Transfer {id} does not exist");
            // Repeating an already-applied receive is not an error and moves nothing.
            if (current.Status == nameof(TransferStatus.Received))
                return Results.Ok(current);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: $"Transfer {id} is {current.Status}; only a Dispatched transfer can be received");
        }

        // Ascending ProductId: same lock order as dispatch, so transfers sharing a location cannot deadlock.
        var lines = await db.TransferLines.AsNoTracking()
            .Where(l => l.TransferId == id)
            .OrderBy(l => l.ProductId)
            .Select(l => new { l.ProductId, ProductName = l.Product.Name, l.Quantity, l.Transfer.ToLocationId })
            .ToListAsync();

        foreach (var line in lines)
        {
            // Plain increment: the seed created a stock row for every location x product, so no existence or limit check is needed.
            var moved = await db.StockLevels
                .Where(s => s.LocationId == line.ToLocationId && s.ProductId == line.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + line.Quantity));

            if (moved == 0)
            {
                // Should be impossible. 500, not 409: the client did nothing wrong and retrying cannot help.
                // Rollback reverts the status flip, so the transfer stays Dispatched and no goods are lost.
                await tx.RollbackAsync();
                logger.LogError("Receive of transfer {TransferId} aborted: no stock row for location {LocationId}, product {ProductId}",
                    id, line.ToLocationId, line.ProductId);
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                    detail: $"Stock record missing for {line.ProductName} at the receiving location; transfer {id} was not received");
            }
        }

        await tx.CommitAsync();
        return Results.Ok(await LoadDtoAsync(db, id));
    }

    private static Task<TransferDto?> LoadDtoAsync(AppDbContext db, int id) =>
        db.Transfers.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(TransferEndpoints.ToDto)
            .FirstOrDefaultAsync();
}
