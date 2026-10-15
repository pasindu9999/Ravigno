using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

public static class TransferDispatchEndpoint
{
    public static IEndpointRouteBuilder MapTransferDispatch(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers/{id:int}/dispatch", DispatchAsync);
        return app;
    }

    private static async Task<IResult> DispatchAsync(int id, AppDbContext db)
    {
        // Disposing without a commit rolls back, so any exception below also undoes the status change.
        await using var tx = await db.Database.BeginTransactionAsync();

        // The guard: only one caller can flip Requested -> Dispatched. A concurrent second caller
        // blocks on this row lock until the first commits, then matches 0 rows.
        var now = DateTime.UtcNow;
        var flipped = await db.Transfers
            .Where(t => t.Id == id && t.Status == TransferStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TransferStatus.Dispatched)
                .SetProperty(t => t.DispatchedAt, now));

        if (flipped == 0)
        {
            await tx.RollbackAsync();
            var current = await LoadDtoAsync(db, id);
            if (current is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"Transfer {id} does not exist");
            // Repeating an already-applied dispatch is not an error and moves nothing.
            if (current.Status == nameof(TransferStatus.Dispatched))
                return Results.Ok(current);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: $"Transfer {id} is {current.Status}; only a Requested transfer can be dispatched");
        }

        // Ascending ProductId: two transfers sharing a sender always lock stock rows in the same order, so they cannot deadlock.
        var lines = await db.TransferLines.AsNoTracking()
            .Where(l => l.TransferId == id)
            .OrderBy(l => l.ProductId)
            .Select(l => new { l.ProductId, ProductName = l.Product.Name, l.Quantity, l.Transfer.FromLocationId })
            .ToListAsync();

        foreach (var line in lines)
        {
            // Check and decrement in one statement, so stock cannot go negative between a read and a write.
            var moved = await db.StockLevels
                .Where(s => s.LocationId == line.FromLocationId && s.ProductId == line.ProductId && s.Quantity >= line.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - line.Quantity));

            if (moved == 0)
            {
                // Undoes the status flip and any earlier lines.
                await tx.RollbackAsync();
                return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                    detail: $"Insufficient stock for {line.ProductName}");
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
