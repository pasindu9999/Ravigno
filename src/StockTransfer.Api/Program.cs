using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Overridable with the ConnectionStrings__Default environment variable (tests, other machines).
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

// Scoped (default): one DbContext per HTTP request.
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DbSeeder.MigrateAndSeedAsync(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        app.Logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapBranchEndpoints();

app.Run();
