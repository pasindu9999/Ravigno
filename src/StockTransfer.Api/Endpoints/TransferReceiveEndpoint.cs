using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

// ReceivedQuantity is nullable so a missing property is a 400, not a silent 0.
public record ReceiveLineRequest(int ProductId, int? ReceivedQuantity);
public record ReceiveRequest(List<ReceiveLineRequest>? Lines, string? Reason);

public static class TransferReceiveEndpoint
{
    private const int MaxReasonLength = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapTransferReceive(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers/{id:int}/receive", ReceiveAsync);
        return app;
    }

    private static async Task<IResult> ReceiveAsync(int id, HttpRequest request, AppDbContext db, ILogger<Program> logger)
    {
        // Read as text, not ContentLength: a chunked or whitespace-only body must count as "no body",
        // because the UI posts receive with no body and no Content-Type.
        string raw;
        using (var reader = new StreamReader(request.Body))
            raw = await reader.ReadToEndAsync();

        ReceiveRequest? body = null;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (!request.HasJsonContentType())
                return Invalid("body", "Content-Type must be application/json");
            try
            {
                body = JsonSerializer.Deserialize<ReceiveRequest>(raw, JsonOptions);
            }
            catch (JsonException ex)
            {
                var path = ex.Path is null or "$" ? "body" : ex.Path.StartsWith("$.") ? ex.Path[2..] : ex.Path;
                var message = path.EndsWith("receivedQuantity", StringComparison.OrdinalIgnoreCase)
                    ? "receivedQuantity must be a whole number"
                    : "Request body is not valid JSON for this endpoint";
                return Invalid(path, message);
            }
        }

        // Validation runs before the transaction. Lines never change after create, so this read cannot go stale,
        // and a bad request returns 400 without taking any row lock.
        var lines = await db.TransferLines.AsNoTracking()
            .Where(l => l.TransferId == id)
            .OrderBy(l => l.ProductId)
            .Select(l => new { l.ProductId, ProductName = l.Product.Name, l.Quantity, l.Transfer.ToLocationId })
            .ToListAsync();
        // Every transfer has at least one line, so none means the transfer does not exist.
        if (lines.Count == 0)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, detail: $"Transfer {id} does not exist");

        // Unlisted lines (and no body at all) receive the full sent quantity.
        var received = lines.ToDictionary(l => l.ProductId, l => l.Quantity);
        var errors = new Dictionary<string, string[]>();
        var seen = new HashSet<int>();
        var reason = body?.Reason?.Trim();
        if (string.IsNullOrEmpty(reason)) reason = null;
        if (reason is { Length: > MaxReasonLength })
            errors["reason"] = [$"Reason must be at most {MaxReasonLength} characters"];

        var requested = body?.Lines ?? [];
        for (var i = 0; i < requested.Count; i++)
        {
            var line = requested[i];
            if (line is null)
            {
                errors[$"lines[{i}]"] = ["Line must not be null"];
                continue;
            }
            var sent = lines.FirstOrDefault(l => l.ProductId == line.ProductId)?.Quantity;
            if (sent is null)
                errors[$"lines[{i}].productId"] = [$"Product {line.ProductId} is not on transfer {id}"];
            else if (!seen.Add(line.ProductId))
                errors[$"lines[{i}].productId"] = [$"Product {line.ProductId} appears more than once"];
            else if (line.ReceivedQuantity is null)
                errors[$"lines[{i}].receivedQuantity"] = ["receivedQuantity is required"];
            else if (line.ReceivedQuantity < 0 || line.ReceivedQuantity > sent)
                errors[$"lines[{i}].receivedQuantity"] = [$"receivedQuantity must be between 0 and the sent quantity ({sent})"];
            else
                received[line.ProductId] = line.ReceivedQuantity.Value;
        }
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, detail: string.Join("; ", errors.Values.SelectMany(v => v)));

        // Disposing without a commit rolls back, so any exception below also undoes the status change.
        await using var tx = await db.Database.BeginTransactionAsync();

        // The guard: only one caller can flip Dispatched -> Received. A concurrent second caller
        // blocks on this row lock until the first commits, then matches 0 rows. The first receive wins.
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
            // Repeating an already-applied receive is not an error and moves nothing; any quantities in this call are ignored.
            if (current.Status == nameof(TransferStatus.Received))
                return Results.Ok(current);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                detail: $"Transfer {id} is {current.Status}; only a Dispatched transfer can be received");
        }

        // Ascending ProductId (lines are already ordered): same lock order as dispatch, so transfers sharing a location cannot deadlock.
        foreach (var line in lines)
        {
            var qty = received[line.ProductId];

            await db.TransferLines
                .Where(l => l.TransferId == id && l.ProductId == line.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.ReceivedQuantity, qty));

            if (qty > 0)
            {
                // Plain increment: the seed created a stock row for every location x product, so no existence or limit check is needed.
                // Only the received quantity is added. The sender is not refunded: the goods left at dispatch.
                var moved = await db.StockLevels
                    .Where(s => s.LocationId == line.ToLocationId && s.ProductId == line.ProductId)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + qty));

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

            if (qty < line.Quantity)
            {
                db.ReceiveDiscrepancies.Add(new ReceiveDiscrepancy
                {
                    TransferId = id,
                    ProductId = line.ProductId,
                    MissingQuantity = line.Quantity - qty,
                    Reason = reason,
                    CreatedAt = now,
                });
            }
        }

        // Inserts the discrepancy rows inside the same transaction as the guard and the stock updates.
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Results.Ok(await LoadDtoAsync(db, id));
    }

    private static Task<TransferDto?> LoadDtoAsync(AppDbContext db, int id) =>
        db.Transfers.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(TransferEndpoints.ToDto)
            .FirstOrDefaultAsync();

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message);
}
