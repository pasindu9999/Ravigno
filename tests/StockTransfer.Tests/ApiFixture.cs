using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockTransfer.Api.Data;

namespace StockTransfer.Tests;

// One per test run: a uniquely named database on the same SQL Server container, the real app started against it.
// Owns the factory instead of inheriting it, because WebApplicationFactory.DisposeAsync returns ValueTask
// and clashes with xUnit v2's IAsyncLifetime.DisposeAsync (Task).
public class ApiFixture : IAsyncLifetime
{
    private const string EnvVar = "ConnectionStrings__Default";

    private string? _originalEnvVar;
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString { get; private set; } = "";
    public string DatabaseName { get; } = $"StockTransfer_Test_{Guid.NewGuid():N}";
    public HttpClient Client { get; private set; } = null!;

    public AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        try
        {
            // Same source as the app: env var wins over appsettings.json. Only the database name changes.
            var config = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(FindSolutionRoot(), "src", "StockTransfer.Api", "appsettings.json"))
                .AddEnvironmentVariables()
                .Build();
            var baseCs = new SqlConnectionStringBuilder(config.GetConnectionString("Default"));
            var master = new SqlConnectionStringBuilder(baseCs.ConnectionString) { InitialCatalog = "master" };
            ConnectionString = new SqlConnectionStringBuilder(baseCs.ConnectionString) { InitialCatalog = DatabaseName }.ConnectionString;

            await ExecuteOnMasterAsync(master.ConnectionString, $"CREATE DATABASE [{DatabaseName}]");

            // WebApplication.CreateBuilder reads environment variables when Program.cs starts, before it calls
            // GetConnectionString, and env vars override appsettings.json. Restored in DisposeAsync.
            _originalEnvVar = Environment.GetEnvironmentVariable(EnvVar);
            Environment.SetEnvironmentVariable(EnvVar, ConnectionString);

            _factory = new WebApplicationFactory<Program>();
            Client = _factory.CreateClient(); // starts the host: Program.cs migrates and seeds the test database

            // Prove the app's own connection points at the test database, not StockTransfer.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var actual = db.Database.GetDbConnection().Database;
            if (actual != DatabaseName)
                throw new InvalidOperationException($"App is using database '{actual}', expected '{DatabaseName}'.");
        }
        catch
        {
            // xUnit does not call DisposeAsync when InitializeAsync throws.
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        Environment.SetEnvironmentVariable(EnvVar, _originalEnvVar);

        if (ConnectionString.Length == 0) return;

        // Pooled connections would keep the database busy; SINGLE_USER ... ROLLBACK IMMEDIATE kicks out any stragglers.
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteOnMasterAsync(master,
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN " +
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{DatabaseName}]; END");
    }

    private static async Task ExecuteOnMasterAsync(string masterConnectionString, string sql)
    {
        await using var conn = new SqlConnection(masterConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockTransfer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("StockTransfer.sln not found above the test output folder.");
    }
}

// One collection for the whole run, so test classes never run in parallel against shared stock.
[CollectionDefinition("Api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;
