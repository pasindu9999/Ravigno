using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Data;
using StockTransfer.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Overridable with the ConnectionStrings__Default environment variable (tests, other machines).
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");

// Scoped (default): one DbContext per HTTP request.
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));

builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await DbSeeder.MigrateAndSeedAsync(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        app.Logger);
}

// Unhandled exceptions and bare 404s become ProblemDetails; the exception text is logged, not returned.
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapBranchEndpoints();
app.MapTransferEndpoints();
app.MapTransferDispatch();
app.MapTransferReceive();
app.MapTransferCancel();
app.MapTransferList();

app.Run();

public partial class Program { }
