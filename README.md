Same Idempotency-Key with a different body returns the original transfer unchanged
Replay reveals a transfer to anyone with the key: auth is out of scope

A dispatch retried after the transfer has been received returns 409.
The list has no paging.
The branchId filter matches From or To.
Invalid list filters return 400 and not an empty list.

## Running the tests

1. `docker compose up -d` and wait until healthy
2. Stop a running API first (`dotnet test` rebuilds the DLLs a running API locks)
3. `dotnet test`

- Tests run the real API in-process against a real SQL Server database named
  `StockTransfer_Test_<guid>` on the same container. It is created before the run and
  dropped after. The dev `StockTransfer` database is never touched (the fixture checks
  `DB_NAME()` and fails if the host connected anywhere else).
- Each test sets its own stock, so none depend on seed quantities.
- If a run is killed, leftover test databases can be dropped with:
  ```sql
  DECLARE @sql nvarchar(max) = N'';
  SELECT @sql += N'ALTER DATABASE ' + QUOTENAME(name) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(name) + N';'
  FROM sys.databases WHERE name LIKE 'StockTransfer[_]Test[_]%';
  EXEC sp_executesql @sql;
  ```

```

## Database notes

- SQL Server 2022 in Docker, port bound to 127.0.0.1 only. The connection string uses
  127.0.0.1 because `localhost` can resolve to ::1, which the container does not serve.
- The `sa` password is for a throwaway local container only. Override the connection string
  with the `ConnectionStrings__Default` environment variable.

## Trade-offs (tests)

- 7 tests cover the rules in the brief. Not covered: cancel happy path, repeated receive or
  cancel, validation cases, list filters, a stock-conservation check.
- The concurrent-dispatch test releases both requests together and repeats 10 rounds. That
  makes a race likely to show up but cannot prove there is none; the guarantee comes from the
  single guarded UPDATE.
- Idempotency has two layers (replay lookup and the unique-index catch). Removing only one
  still passes the test, by design.
```
