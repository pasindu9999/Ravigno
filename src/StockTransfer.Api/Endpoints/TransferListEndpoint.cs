using System.Globalization;
using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

public static class TransferListEndpoint
{
    public static IEndpointRouteBuilder MapTransferList(this IEndpointRouteBuilder app)
    {
        app.MapGet("/transfers", ListAsync);
        return app;
    }

    // Both values are bound as strings and parsed by hand, so every bad value becomes our own 400 ProblemDetails.
    private static async Task<IResult> ListAsync(string? branchId, string? status, AppDbContext db)
    {
        // Blank means "not supplied" (e.g. an "All" option in a filter dropdown).
        int? branch = null;
        if (!string.IsNullOrWhiteSpace(branchId))
        {
            if (!int.TryParse(branchId, NumberStyles.None, CultureInfo.InvariantCulture, out var b))
                return Invalid("branchId", "branchId must be a whole number");
            if (!await db.Locations.AnyAsync(l => l.Id == b))
                return Invalid("branchId", $"Branch {b} does not exist");
            branch = b;
        }

        TransferStatus? wanted = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            // Match by name, not Enum.TryParse: TryParse also accepts "1" and "Requested,Received".
            var names = Enum.GetNames<TransferStatus>();
            var match = names.FirstOrDefault(n => string.Equals(n, status, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return Invalid("status", $"status must be one of: {string.Join(", ", names)}");
            wanted = Enum.Parse<TransferStatus>(match);
        }

        var query = db.Transfers.AsNoTracking();
        if (branch is { } id)
            query = query.Where(t => t.FromLocationId == id || t.ToLocationId == id);
        if (wanted is { } s)
            query = query.Where(t => t.Status == s);

        // No paging: returns every match. Fine at this scale, unbounded in general.
        var transfers = await query
            .OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
            .Select(TransferEndpoints.ToDto)
            .ToListAsync();
        return Results.Ok(transfers);
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message);
}
