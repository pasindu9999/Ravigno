using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Endpoints;

public record CreateTransferLine(int ProductId, int Quantity);
public record CreateTransferRequest(int FromLocationId, int ToLocationId, List<CreateTransferLine>? Lines);

public record LocationRefDto(int Id, string Name);
public record TransferLineDto(int ProductId, string ProductName, int Quantity);
public record TransferDto(
    int Id,
    LocationRefDto From,
    LocationRefDto To,
    string Status,
    string? IdempotencyKey,
    DateTime CreatedAt,
    List<TransferLineDto> Lines);

public static class TransferEndpoints
{
    private const int MaxKeyLength = 100;
    private const int MaxQuantity = 1_000_000;

    // Shared response shape: POST uses it now, the T6 list endpoint can .Select(ToDto) too.
    public static readonly Expression<Func<Transfer, TransferDto>> ToDto = t => new TransferDto(
        t.Id,
        new LocationRefDto(t.FromLocation.Id, t.FromLocation.Name),
        new LocationRefDto(t.ToLocation.Id, t.ToLocation.Name),
        t.Status.ToString(),
        t.IdempotencyKey,
        t.CreatedAt,
        t.Lines.OrderBy(l => l.ProductId)
            .Select(l => new TransferLineDto(l.ProductId, l.Product.Name, l.Quantity))
            .ToList());

    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/transfers", CreateTransferAsync);
        return app;
    }

    private static async Task<IResult> CreateTransferAsync(HttpRequest request, AppDbContext db)
    {
        // Blank or whitespace-only header = no key.
        var rawKey = request.Headers["Idempotency-Key"].ToString().Trim();
        var key = rawKey.Length == 0 ? null : rawKey;
        if (key is { Length: > MaxKeyLength })
            return Invalid("Idempotency-Key", $"Idempotency-Key must be at most {MaxKeyLength} characters");

        // Replay first, before the body is even read: a repeated key with a different body returns the original.
        if (key is not null && await FindByKeyAsync(db, key) is { } replay)
            return Results.Ok(replay);

        // The body is read by hand (not bound as a parameter) so every binding failure becomes our own 400 ProblemDetails.
        if (!request.HasJsonContentType())
            return Invalid("body", "Content-Type must be application/json");

        CreateTransferRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<CreateTransferRequest>();
        }
        catch (JsonException ex)
        {
            // ex.Path looks like "$.lines[0].quantity"; strip "$." to match the keys used below.
            var path = ex.Path is null or "$" ? "body" : ex.Path.StartsWith("$.") ? ex.Path[2..] : ex.Path;
            var message = path.EndsWith("quantity", StringComparison.OrdinalIgnoreCase)
                ? "Quantity must be a whole number"
                : "Request body is not valid JSON for this endpoint";
            return Invalid(path, message);
        }

        if (body is null)
            return Invalid("body", "Request body is required");

        var errors = new Dictionary<string, string[]>();
        var lines = body.Lines ?? [];

        if (lines.Count == 0)
            errors["lines"] = ["At least one line is required"];
        if (body.FromLocationId == body.ToLocationId)
            errors["toLocationId"] = ["From and to locations must be different"];

        var seen = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line is null)
            {
                errors[$"lines[{i}]"] = ["Line must not be null"];
                continue;
            }
            if (line.Quantity is <= 0 or > MaxQuantity)
                errors[$"lines[{i}].quantity"] = ["Quantity must be a whole number between 1 and 1,000,000"];
            if (!seen.Add(line.ProductId))
                errors[$"lines[{i}].productId"] = [$"Product {line.ProductId} appears more than once"];
        }

        var locationIds = new[] { body.FromLocationId, body.ToLocationId }.Distinct().ToList();
        var knownLocations = await db.Locations.Where(l => locationIds.Contains(l.Id)).Select(l => l.Id).ToListAsync();
        if (!knownLocations.Contains(body.FromLocationId))
            errors.TryAdd("fromLocationId", [$"Location {body.FromLocationId} does not exist"]);
        if (!knownLocations.Contains(body.ToLocationId))
            errors.TryAdd("toLocationId", [$"Location {body.ToLocationId} does not exist"]);

        var productIds = lines.Where(l => l != null).Select(l => l.ProductId).Distinct().ToList();
        var knownProducts = await db.Products.Where(p => productIds.Contains(p.Id)).Select(p => p.Id).ToListAsync();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] != null && !knownProducts.Contains(lines[i].ProductId))
                errors.TryAdd($"lines[{i}].productId", [$"Product {lines[i].ProductId} does not exist"]);
        }

        if (errors.Count > 0)
            return Results.ValidationProblem(errors, detail: string.Join("; ", errors.Values.SelectMany(v => v)));

        var transfer = new Transfer
        {
            FromLocationId = body.FromLocationId,
            ToLocationId = body.ToLocationId,
            Status = TransferStatus.Requested,
            IdempotencyKey = key,
            CreatedAt = DateTime.UtcNow,
            Lines = lines.Select(l => new TransferLine { ProductId = l.ProductId, Quantity = l.Quantity }).ToList()
        };
        db.Transfers.Add(transfer);

        try
        {
            // One SaveChanges = one transaction: the header and its lines commit together or not at all.
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (key is not null && IsIdempotencyKeyViolation(ex))
        {
            // Lost the race: another request committed this key between our lookup and insert.
            // Drop the failed Added entities, then reload the winner's committed row.
            db.ChangeTracker.Clear();
            return Results.Ok((await FindByKeyAsync(db, key))!); // the violation proves the row exists
        }

        var created = await db.Transfers.AsNoTracking()
            .Where(t => t.Id == transfer.Id)
            .Select(ToDto)
            .FirstAsync();
        // Results.Json, not Results.Created: there is no GET /transfers/{id}, so no Location header.
        return Results.Json(created, statusCode: StatusCodes.Status201Created);
    }

    private static Task<TransferDto?> FindByKeyAsync(AppDbContext db, string key) =>
        db.Transfers.AsNoTracking()
            .Where(t => t.IdempotencyKey == key)
            .Select(ToDto)
            .FirstOrDefaultAsync();

    // 2601 = duplicate key in a unique index, 2627 = in a unique constraint/PK. The number alone is not enough
    // (PK_TransferLines also raises 2627), and SqlException has no index-name property, so match the name in the message.
    // The name comes from the InitialCreate migration.
    private static bool IsIdempotencyKeyViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 } sql
        && sql.Message.Contains("IX_Transfers_IdempotencyKey");

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }, detail: message);
}
