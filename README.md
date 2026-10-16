# Branch Stock Transfer

A small system for moving stock between a central warehouse and three branches (Colombo, Kandy, Galle). A branch requests a transfer of products, the sender dispatches it (stock leaves the sender), and the receiver receives it (stock arrives). Goods in transit are in neither location, so total stock plus in-transit stays constant. Stack: ASP.NET Core Web API (.NET 8) with EF Core on SQL Server 2022 (Docker), and a one-screen React + TypeScript client (Vite). Tests run xUnit against a real SQL Server database.

## Prerequisites

- .NET 8 SDK (all projects target `net8.0`; there is no `global.json`)
- Docker (with Compose) for SQL Server 2022
- Node.js **20.19+ or 22.12+**. The client uses Vite 8, whose lockfile entry declares `"node": "^20.19.0 || >=22.12.0"`.

## Run

PowerShell, from the repo root:

```powershell
docker compose up -d
docker compose ps                  # repeat until the db service shows "healthy"
dotnet tool restore
dotnet run --project src/StockTransfer.Api --launch-profile http
```

The API listens on http://localhost:5053 (Swagger UI at `/swagger`). On startup it applies migrations and seeds data; if SQL Server is still starting it retries (6 attempts, 5 s apart).

The `http` profile is required: the Vite dev proxy forwards `/api/*` to `http://localhost:5053` (`client/vite.config.ts`).

`dotnet tool restore` installs `dotnet-ef` (pinned in `dotnet-tools.json`). It is only needed for `dotnet ef` commands; running the API does not need it.

In a second terminal:

```powershell
cd client
npm ci
npm run dev
```

Open the URL Vite prints (Vite's default is http://localhost:5173; use whatever it prints).

## Running the tests

1. `docker compose up -d` and wait until healthy
2. Stop a running API first (`dotnet test` rebuilds DLLs that a running API locks)
3. `dotnet test`

- Tests start the real API in-process against a real SQL Server database named `StockTransfer_Test_<guid>` on the same container. It is created before the run and dropped after. The dev `StockTransfer` database is never touched (the fixture checks the app's database name and fails if it connected anywhere else).
- Each test sets its own stock, so none depend on seed quantities.
- If a run is killed, leftover test databases can be dropped by running this against the container (for example in SSMS or `sqlcmd`):

```sql
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
FROM sys.databases WHERE name LIKE 'StockTransfer[_]Test[_]%';
EXEC sp_executesql @sql;
```

## API

| Method | Path                           | What it does                                                                             | Status codes                                                                            |
| ------ | ------------------------------ | ---------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------- |
| GET    | `/branches`                    | List locations (id, name)                                                                | 200                                                                                     |
| GET    | `/branches/{id}/stock`         | Stock of every product at one location                                                   | 200, 404                                                                                |
| POST   | `/transfers`                   | Create a transfer (status Requested, no stock effect). Optional `Idempotency-Key` header | 201 created, 200 replay of an existing key, 400                                         |
| GET    | `/transfers?branchId=&status=` | List transfers, newest first. `branchId` matches From or To                              | 200, 400                                                                                |
| POST   | `/transfers/{id}/dispatch`     | Requested to Dispatched; sender stock decreases                                          | 200 (also if already Dispatched), 404, 409                                              |
| POST   | `/transfers/{id}/receive`      | Dispatched to Received; receiver stock increases                                         | 200 (also if already Received), 404, 409, 500 (receiver stock row missing, rolled back) |
| POST   | `/transfers/{id}/cancel`       | Requested to Cancelled; no stock effect                                                  | 200 (also if already Cancelled), 404, 409                                               |

Errors are ProblemDetails with a readable message in `detail`. 409 means insufficient stock (`Insufficient stock for <product>`) or a wrong status.

## How it works

- **State machine:** Requested, then Dispatched, then Received; or Requested, then Cancelled. Nothing else is allowed.
- **Guarded UPDATE for status:** each action starts with `UPDATE Transfers SET Status = ... WHERE Id = @id AND Status = <expected>` (via `ExecuteUpdateAsync`). A concurrent second caller blocks on the row lock, then matches 0 rows. On 0 rows the transfer is reloaded: already in the target state = 200 with the current state, missing = 404, anything else = 409.
- **Conditional UPDATE for stock:** dispatch runs `Quantity = Quantity - @n WHERE LocationId = @from AND ProductId = @p AND Quantity >= @n`. 0 rows rolls back the whole transaction (including the status change) and returns 409. Receive adds at the To location; the seed created a stock row for every location x product, so it is a plain UPDATE. Stock is never read and then written from C#.
- **One transaction per action:** dispatch and receive wrap the status flip and all line updates in one transaction. Cancel is a single UPDATE and needs none.
- **Ascending ProductId order:** lines are applied in ProductId order so two transfers that share a location lock stock rows in the same order and cannot deadlock.
- **DB CHECK as backstop:** `CK_StockLevels_Quantity_NonNegative` (`Quantity >= 0`) stops any other write path going negative. `CK_TransferLines_Quantity_Range` (1 to 1,000,000) does the same for lines.
- **Idempotency, two layers:** (1) `POST /transfers` looks up the `Idempotency-Key` first and returns the existing transfer. (2) A filtered unique index (`IX_Transfers_IdempotencyKey`, `WHERE [IdempotencyKey] IS NOT NULL`) catches two requests that both passed the lookup; the loser catches the unique violation and returns the winner's row. Removing either layer alone still passes the test, by design.

## Assumptions

- The same `Idempotency-Key` with a different body returns the original transfer unchanged (the lookup happens before the body is validated).
- Repeating dispatch, receive or cancel returns 200 with the current state and moves nothing. Any other invalid transition is 409, including dispatch after Received.
- Requesting does not reserve stock. The stock figure in the UI is advisory; the check at dispatch is authoritative.
- The warehouse is a location like any other and can be a destination.
- Quantity is a whole number from 1 to 1,000,000. A duplicate product in one request is 400.
- A blank or whitespace `Idempotency-Key` means no key. A key longer than 100 characters is 400.
- The `branchId` list filter matches transfers where the branch is From or To.
- Invalid list filters (non-numeric or unknown `branchId`, unknown `status`) are 400, not an empty list.
- No authentication, so anyone with an idempotency key can retrieve that transfer by replaying it.
- Timestamps are UTC.
- Identity values can jump after a container restart (SQL Server identity caching).

## Trade-offs

- Migrate and seed run on startup, with a manual retry loop for connection failures. `EnableRetryOnFailure` is not used because it does not combine with the user-started transactions in dispatch and receive.
- `ExecuteUpdateAsync` instead of raw SQL. The generated SQL is the same guarded UPDATE, and it stays type-checked.
- Dispatch, receive and cancel repeat similar code on purpose. Each is short and can be read on its own; a shared helper is on the "another day" list.
- No paging on `GET /transfers`; it returns every match.
- The concurrent-dispatch test releases two requests together and repeats 10 rounds. That makes a race likely to show up but cannot prove there is none; the guarantee comes from the single guarded UPDATE.
- Eight tests only: the seven in the brief plus a repeated receive. Not covered: cancel happy path, repeated cancel, validation cases, list filters, a stock-conservation check, the client.
- The `sa` password in `docker-compose.yml` and `appsettings.json` is for a throwaway local container only. SQL Server is bound to 127.0.0.1 so it is not reachable from other machines. The connection string uses 127.0.0.1 because `localhost` can resolve to `::1`, which the container does not serve. Override it with the `ConnectionStrings__Default` environment variable.

## With another day

- Partial receive: `ReceivedQuantity` on the transfer line plus a difference record for damaged or missing goods.
- A shared `TransitionAsync` for dispatch, receive and cancel.
- Paging on the transfer list.
- Authentication and roles (and scoping idempotency keys to a user).
- More tests: cancel happy path, repeated cancel, validation, list filters, stock conservation, client tests.
- A CHECK constraint on `FromLocationId <> ToLocationId`.
- A cap on the number of lines per request.
- Run migrations as a separate step instead of inside the app.

## Time spent

<fill in: 2 hours 45 minutes>
