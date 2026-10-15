using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

public static class TransferCancelEndpoint
{
    public static IEndpointRouteBuilder MapTransferCancel(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers/{id:int}/cancel", CancelAsync);
        return app;
    }

    private static async Task<IResult> CancelAsync(int id, AppDbContext db)
    {
        // No explicit transaction: this is the only write, and one UPDATE statement is atomic on its own.
        // Same guard idea as dispatch: only a Requested transfer can be cancelled, and a concurrent
        // dispatch or cancel blocks on the row lock, then re-checks the status.
        var now = DateTime.UtcNow;
        var flipped = await db.Transfers
            .Where(t => t.Id == id && t.Status == TransferStatus.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, TransferStatus.Cancelled)
                .SetProperty(t => t.CancelledAt, now));

        if (flipped == 0)
        {
            var current = await LoadDtoAsync(db, id);
            if (current is null)
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"Transfer {id} does not exist");
            // Repeating an already-applied cancel is not an error.
            if (current.Status == nameof(TransferStatus.Cancelled))
                return Results.Ok(current);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: $"Transfer {id} is {current.Status}; only a Requested transfer can be cancelled");
        }

        return Results.Ok(await LoadDtoAsync(db, id));
    }

    private static Task<TransferDto?> LoadDtoAsync(AppDbContext db, int id) =>
        db.Transfers.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(TransferEndpoints.ToDto)
            .FirstOrDefaultAsync();
}
