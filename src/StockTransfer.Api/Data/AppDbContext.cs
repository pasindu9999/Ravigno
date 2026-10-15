using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using StockTransfer.Api.Models;

namespace StockTransfer.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    // DbSet names are the table names.
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLevel> StockLevels => Set<StockLevel>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<TransferLine> TransferLines => Set<TransferLine>();

    // SQL Server datetime2 comes back with Kind=Unspecified, which serialises without a trailing Z.
    // Mark every DateTime as UTC on read; nothing changes on write and the column type stays the same (no migration).
    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        c.Properties<DateTime>().HaveConversion<UtcConverter>();
        c.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTime, DateTime>(
        v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(
        v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<Product>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<StockLevel>(e =>
        {
            // The composite PK is the unique (LocationId, ProductId) index.
            e.HasKey(x => new { x.LocationId, x.ProductId });
            e.HasOne(x => x.Location).WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            // Backstop only: dispatch uses a conditional UPDATE, this stops any other write path going negative.
            e.ToTable(t => t.HasCheckConstraint("CK_StockLevels_Quantity_NonNegative", "[Quantity] >= 0"));
        });

        b.Entity<Transfer>(e =>
        {
            // Stored as text ('Requested', ...) so SSMS and the SQL status guards can read it.
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            // Unique only when a key is supplied; many transfers may have no key.
            e.HasIndex(x => x.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            // Restrict: SQL Server rejects two cascade paths to Locations.
            e.HasOne(x => x.FromLocation).WithMany().HasForeignKey(x => x.FromLocationId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ToLocation).WithMany().HasForeignKey(x => x.ToLocationId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TransferLine>(e =>
        {
            // One line per product per transfer.
            e.HasKey(x => new { x.TransferId, x.ProductId });
            e.HasOne(x => x.Transfer).WithMany(x => x.Lines).HasForeignKey(x => x.TransferId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_TransferLines_Quantity_Range", "[Quantity] > 0 AND [Quantity] <= 1000000"));
        });
    }
}
