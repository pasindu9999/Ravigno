using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;

namespace StockTransfer.Api.Endpoints;

public record BranchDto(int Id, string Name);

public static class BranchListEndpoint
{
    public static IEndpointRouteBuilder MapBranchList(this IEndpointRouteBuilder app)
    {
        app.MapGet("/branches", async (AppDbContext db) =>
            Results.Ok(await db.Locations.AsNoTracking()
                .OrderBy(l => l.Id)
                .Select(l => new BranchDto(l.Id, l.Name))
                .ToListAsync()));
        return app;
    }
}
