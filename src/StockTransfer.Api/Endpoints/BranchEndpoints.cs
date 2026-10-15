using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;

namespace StockTransfer.Api.Endpoints;

public record StockItemDto(int ProductId, string ProductName, int Quantity);
public record BranchStockDto(int BranchId, string BranchName, List<StockItemDto> Items);

public static class BranchEndpoints
{
    public static IEndpointRouteBuilder MapBranchEndpoints(this IEndpointRouteBuilder app)
    {
        // {id:int}: a non-numeric id matches no route and returns a plain 404.
        app.MapGet("/branches/{id:int}/stock", async (int id, AppDbContext db) =>
        {
            var branch = await db.Locations.AsNoTracking()
                .Where(l => l.Id == id)
                .Select(l => new BranchStockDto(
                    l.Id,
                    l.Name,
                    db.StockLevels
                        .Where(s => s.LocationId == l.Id)
                        .OrderBy(s => s.ProductId)
                        .Select(s => new StockItemDto(s.ProductId, s.Product.Name, s.Quantity))
                        .ToList()))
                .FirstOrDefaultAsync();

            // Error pattern for T4-T6: Results.Problem(statusCode, title, detail).
            return branch is null
                ? Results.Problem(statusCode: 404, title: "Not Found", detail: $"Branch {id} not found")
                : Results.Ok(branch);
        });

        return app;
    }
}
