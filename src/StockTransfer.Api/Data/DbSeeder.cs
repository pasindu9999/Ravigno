using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Data;

public static class DbSeeder
{
    private const int MigrateAttempts = 6;
    private static readonly TimeSpan MigrateDelay = TimeSpan.FromSeconds(5);

    private static readonly string[] LocationNames = ["Central Warehouse", "Colombo", "Kandy", "Galle"];
    private static readonly string[] ProductNames = ["Rice 5kg", "Sugar 1kg", "Tea 500g", "Cooking Oil 1L", "Soap Bar"];

    // Initial quantity per [location][product], same order as the arrays above. Applied on insert only.
    private static readonly int[][] InitialQuantity =
    [
        [500, 300, 200, 150, 400], // Central Warehouse
        [50, 20, 0, 5, 30],        // Colombo
        [30, 0, 15, 10, 0],        // Kandy
        [5, 12, 0, 8, 20],         // Galle
    ];

    public static async Task MigrateAndSeedAsync(AppDbContext db, ILogger logger)
    {
        await MigrateWithRetryAsync(db, logger);
        await SeedAsync(db);
    }

    // The SQL Server container may still be starting, so retry connection failures a few times.
    private static async Task MigrateWithRetryAsync(AppDbContext db, ILogger logger)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                return;
            }
            catch (SqlException ex) when (attempt < MigrateAttempts)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}/{Max}): {Message}", attempt, MigrateAttempts, ex.Message);
                await Task.Delay(MigrateDelay);
            }
        }
    }

    // Safe to run on every startup: each row is added only if its natural key is missing.
    private static async Task SeedAsync(AppDbContext db)
    {
        var locations = await db.Locations.ToListAsync();
        var products = await db.Products.ToListAsync();

        foreach (var name in LocationNames.Where(n => locations.All(l => l.Name != n)))
        {
            var location = new Location { Name = name };
            db.Locations.Add(location);
            locations.Add(location);
        }

        foreach (var name in ProductNames.Where(n => products.All(p => p.Name != n)))
        {
            var product = new Product { Name = name };
            db.Products.Add(product);
            products.Add(product);
        }

        var existing = (await db.StockLevels.Select(s => new { s.LocationId, s.ProductId }).ToListAsync())
            .Select(s => (s.LocationId, s.ProductId))
            .ToHashSet();

        for (var l = 0; l < LocationNames.Length; l++)
        {
            for (var p = 0; p < ProductNames.Length; p++)
            {
                var location = locations.Single(x => x.Name == LocationNames[l]);
                var product = products.Single(x => x.Name == ProductNames[p]);

                // Ids of rows added above are still 0 here, so they never match: correct, they have no stock rows yet.
                if (existing.Contains((location.Id, product.Id))) continue;

                // Navigation properties let one SaveChanges insert locations, products and stock rows with their generated ids.
                db.StockLevels.Add(new StockLevel { Location = location, Product = product, Quantity = InitialQuantity[l][p] });
            }
        }

        // A single SaveChanges is a single transaction: all or nothing.
        await db.SaveChangesAsync();
    }
}
