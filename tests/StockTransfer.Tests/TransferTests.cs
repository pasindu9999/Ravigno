using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StockTransfer.Api.Endpoints;
using StockTransfer.Api.Models;

namespace StockTransfer.Tests;

[Collection("Api")]
public class TransferTests(ApiFixture api)
{
    private const string Warehouse = "Central Warehouse";
    private const string Colombo = "Colombo";
    private const string Rice = "Rice 5kg";
    private const string Sugar = "Sugar 1kg";

    [Fact]
    public async Task Dispatch_WithInsufficientStock_Returns409AndChangesNothing()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo);
        // Dispatch processes lines in ascending ProductId, so the second product here is the second line.
        await using var db = api.NewDb();
        var products = await db.Products.Where(p => p.Name == Rice || p.Name == Sugar).OrderBy(p => p.Id).ToListAsync();
        var (first, second) = (products[0], products[1]);

        await SetStock(from, first.Id, 10);
        await SetStock(from, second.Id, 3);
        await SetStock(to, first.Id, 0);
        await SetStock(to, second.Id, 0);
        var transfer = await Create(from, to, (first.Id, 4), (second.Id, 5)); // only line 2 is short

        var response = await Dispatch(transfer.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"Insufficient stock for {second.Name}", await Detail(response));
        Assert.Equal(10, await Stock(from, first.Id)); // line 1's decrement was rolled back
        Assert.Equal(3, await Stock(from, second.Id));
        Assert.Equal(0, await Stock(to, first.Id));
        Assert.Equal(0, await Stock(to, second.Id));
        Assert.Equal(TransferStatus.Requested, await StatusOf(transfer.Id)); // the status flip was rolled back too
    }

    [Fact]
    public async Task Dispatch_Twice_MovesStockOnce()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 0);
        var transfer = await Create(from, to, (rice, 4));

        var first = await Dispatch(transfer.Id);
        var second = await Dispatch(transfer.Id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Dispatched", (await second.Content.ReadFromJsonAsync<TransferDto>())!.Status);
        Assert.Equal(6, await Stock(from, rice)); // 10 - 4, not 10 - 8
        Assert.Equal(0, await Stock(to, rice)); // in transit: in neither location
        Assert.Equal(TransferStatus.Dispatched, await StatusOf(transfer.Id));
    }

    [Fact]
    public async Task Dispatch_ConcurrentRequests_MoveStockOnce()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);

        for (var round = 1; round <= 10; round++)
        {
            await SetStock(from, rice, 10);
            var transfer = await Create(from, to, (rice, 4));

            // Both requests wait at the gate and are released together, so they overlap on the transfer row lock.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new CountdownEvent(2);
            var calls = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                ready.Signal();
                await gate.Task;
                return await Dispatch(transfer.Id);
            })).ToArray();
            await Task.Run(ready.Wait);
            gate.SetResult();
            var responses = await Task.WhenAll(calls);

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var stock = await Stock(from, rice);
            Assert.True(stock == 6, $"round {round}: sender stock is {stock}, expected 6 (moved once)");
            Assert.Equal(TransferStatus.Dispatched, await StatusOf(transfer.Id));
        }
    }

    [Fact]
    public async Task Receive_BeforeDispatch_Returns409AndChangesNothing()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 0);
        var transfer = await Create(from, to, (rice, 4));

        var response = await Receive(transfer.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(10, await Stock(from, rice));
        Assert.Equal(0, await Stock(to, rice));
        Assert.Equal(TransferStatus.Requested, await StatusOf(transfer.Id));
    }

    [Fact]
    public async Task Cancel_AfterDispatch_Returns409AndStaysDispatched()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 0);
        var transfer = await Create(from, to, (rice, 4));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        var response = await Cancel(transfer.Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(TransferStatus.Dispatched, await StatusOf(transfer.Id));
        Assert.Equal(6, await Stock(from, rice)); // cancel did not give the goods back
        Assert.Equal(0, await Stock(to, rice));
    }

    [Fact]
    public async Task Receive_AfterDispatch_AddsLineQuantitiesToReceiver()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo);
        int rice = await ProductId(Rice), sugar = await ProductId(Sugar);
        await SetStock(from, rice, 10);
        await SetStock(from, sugar, 20);
        await SetStock(to, rice, 3);
        await SetStock(to, sugar, 5);
        var transfer = await Create(from, to, (rice, 4), (sugar, 7));

        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);
        Assert.Equal(6, await Stock(from, rice)); // sender down once, at dispatch
        Assert.Equal(13, await Stock(from, sugar));
        Assert.Equal(3, await Stock(to, rice)); // receiver unchanged while in transit
        Assert.Equal(5, await Stock(to, sugar));

        Assert.Equal(HttpStatusCode.OK, (await Receive(transfer.Id)).StatusCode);
        Assert.Equal(7, await Stock(to, rice)); // 3 + 4
        Assert.Equal(12, await Stock(to, sugar)); // 5 + 7
        Assert.Equal(6, await Stock(from, rice)); // sender not touched again
        Assert.Equal(13, await Stock(from, sugar));
        Assert.Equal(TransferStatus.Received, await StatusOf(transfer.Id));
    }

    [Fact]
    public async Task Receive_Twice_MovesStockOnce()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 3);
        var transfer = await Create(from, to, (rice, 4));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        var first = await Receive(transfer.Id);
        var second = await Receive(transfer.Id);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("Received", (await second.Content.ReadFromJsonAsync<TransferDto>())!.Status);
        Assert.Equal(7, await Stock(to, rice)); // 3 + 4, not 3 + 8
        Assert.Equal(6, await Stock(from, rice));
        Assert.Equal(TransferStatus.Received, await StatusOf(transfer.Id));
    }

    [Fact]
    public async Task Receive_Partial_AddsOnlyReceivedQuantityAndRecordsDiscrepancy()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo);
        int rice = await ProductId(Rice), sugar = await ProductId(Sugar);
        await SetStock(from, rice, 10);
        await SetStock(from, sugar, 20);
        await SetStock(to, rice, 3);
        await SetStock(to, sugar, 5);
        var transfer = await Create(from, to, (rice, 4), (sugar, 7));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        // Only rice is listed; sugar defaults to the full 7.
        var response = await ReceivePartial(transfer.Id, "1 box crushed", (rice, 3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TransferDto>())!;
        Assert.Equal(3, dto.Lines.Single(l => l.ProductId == rice).ReceivedQuantity);
        Assert.Equal(7, dto.Lines.Single(l => l.ProductId == sugar).ReceivedQuantity);
        Assert.Equal(6, await Stock(to, rice)); // 3 + 3 received, not 3 + 4 sent
        Assert.Equal(12, await Stock(to, sugar)); // 5 + 7
        Assert.Equal(6, await Stock(from, rice)); // sender not refunded for the missing unit
        Assert.Equal(13, await Stock(from, sugar));
        var discrepancy = Assert.Single(await Discrepancies(transfer.Id)); // none for sugar
        Assert.Equal((rice, 1, "1 box crushed"), (discrepancy.ProductId, discrepancy.MissingQuantity, discrepancy.Reason));
        Assert.Equal(3, await ReceivedQty(transfer.Id, rice));
        Assert.Equal(7, await ReceivedQty(transfer.Id, sugar));
        Assert.Equal(TransferStatus.Received, await StatusOf(transfer.Id));
    }

    [Fact]
    public async Task Receive_MoreThanSent_Returns400AndChangesNothing()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 3);
        var transfer = await Create(from, to, (rice, 4));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        var response = await ReceivePartial(transfer.Id, null, (rice, 5));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("between 0 and the sent quantity (4)", await Detail(response));
        Assert.Equal(TransferStatus.Dispatched, await StatusOf(transfer.Id));
        Assert.Equal(3, await Stock(to, rice));
        Assert.Equal(6, await Stock(from, rice));
        Assert.Null(await ReceivedQty(transfer.Id, rice));
        Assert.Empty(await Discrepancies(transfer.Id));
    }

    [Fact]
    public async Task Receive_PartialTwice_MovesStockOnce()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 3);
        var transfer = await Create(from, to, (rice, 4));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        var first = await ReceivePartial(transfer.Id, "damaged", (rice, 3));
        var second = await ReceivePartial(transfer.Id, "ignored", (rice, 4)); // first receive wins

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, (await second.Content.ReadFromJsonAsync<TransferDto>())!.Lines.Single().ReceivedQuantity);
        Assert.Equal(6, await Stock(to, rice)); // 3 + 3, not 3 + 3 + 4
        Assert.Equal(3, await ReceivedQty(transfer.Id, rice));
        var discrepancy = Assert.Single(await Discrepancies(transfer.Id));
        Assert.Equal((1, "damaged"), (discrepancy.MissingQuantity, discrepancy.Reason));
    }

    [Fact]
    public async Task Receive_WithNoBody_AddsFullQuantity()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        await SetStock(from, rice, 10);
        await SetStock(to, rice, 3);
        var transfer = await Create(from, to, (rice, 4));
        Assert.Equal(HttpStatusCode.OK, (await Dispatch(transfer.Id)).StatusCode);

        var response = await Receive(transfer.Id); // no body, no Content-Type, like the UI

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(7, await Stock(to, rice));
        Assert.Equal(4, await ReceivedQty(transfer.Id, rice));
        Assert.Empty(await Discrepancies(transfer.Id));
    }

    [Fact]
    public async Task CreateTransfer_WithSameIdempotencyKeyTwice_CreatesOneTransfer()
    {
        int from = await LocationId(Warehouse), to = await LocationId(Colombo), rice = await ProductId(Rice);
        var key = Guid.NewGuid().ToString();
        await using var db = api.NewDb();
        var before = await db.Transfers.CountAsync();

        var first = await PostTransfer(from, to, [(rice, 1)], key);
        var second = await PostTransfer(from, to, [(rice, 1)], key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<TransferDto>())!.Id,
            (await second.Content.ReadFromJsonAsync<TransferDto>())!.Id);
        Assert.Equal(1, await db.Transfers.CountAsync(t => t.IdempotencyKey == key));
        Assert.Equal(before + 1, await db.Transfers.CountAsync());
    }

    // --- helpers ---

    private async Task<int> LocationId(string name)
    {
        await using var db = api.NewDb();
        return await db.Locations.Where(l => l.Name == name).Select(l => l.Id).SingleAsync();
    }

    private async Task<int> ProductId(string name)
    {
        await using var db = api.NewDb();
        return await db.Products.Where(p => p.Name == name).Select(p => p.Id).SingleAsync();
    }

    private async Task SetStock(int locationId, int productId, int quantity)
    {
        await using var db = api.NewDb();
        var rows = await db.StockLevels
            .Where(s => s.LocationId == locationId && s.ProductId == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, quantity));
        Assert.Equal(1, rows); // the seed creates a row for every location x product
    }

    private async Task<int> Stock(int locationId, int productId)
    {
        await using var db = api.NewDb();
        return await db.StockLevels
            .Where(s => s.LocationId == locationId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync();
    }

    private async Task<int?> ReceivedQty(int transferId, int productId)
    {
        await using var db = api.NewDb();
        return await db.TransferLines
            .Where(l => l.TransferId == transferId && l.ProductId == productId)
            .Select(l => l.ReceivedQuantity).SingleAsync();
    }

    private async Task<List<ReceiveDiscrepancy>> Discrepancies(int transferId)
    {
        await using var db = api.NewDb();
        return await db.ReceiveDiscrepancies.Where(d => d.TransferId == transferId).OrderBy(d => d.ProductId).ToListAsync();
    }

    private async Task<TransferStatus> StatusOf(int transferId)
    {
        await using var db = api.NewDb();
        return await db.Transfers.Where(t => t.Id == transferId).Select(t => t.Status).SingleAsync();
    }

    private Task<HttpResponseMessage> PostTransfer(int from, int to, (int Product, int Qty)[] lines, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/transfers")
        {
            Content = JsonContent.Create(new
            {
                fromLocationId = from,
                toLocationId = to,
                lines = lines.Select(l => new { productId = l.Product, quantity = l.Qty }),
            }),
        };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return api.Client.SendAsync(request);
    }

    private async Task<TransferDto> Create(int from, int to, params (int Product, int Qty)[] lines)
    {
        var response = await PostTransfer(from, to, lines);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TransferDto>())!;
    }

    private Task<HttpResponseMessage> Dispatch(int id) => api.Client.PostAsync($"/transfers/{id}/dispatch", null);
    private Task<HttpResponseMessage> Receive(int id) => api.Client.PostAsync($"/transfers/{id}/receive", null);
    private Task<HttpResponseMessage> ReceivePartial(int id, string? reason, params (int Product, int Received)[] lines) =>
        api.Client.PostAsJsonAsync($"/transfers/{id}/receive", new
        {
            reason,
            lines = lines.Select(l => new { productId = l.Product, receivedQuantity = l.Received }),
        });
    private Task<HttpResponseMessage> Cancel(int id) => api.Client.PostAsync($"/transfers/{id}/cancel", null);

    private static async Task<string?> Detail(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString();
}
