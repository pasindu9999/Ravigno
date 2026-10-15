# Branch Stock Transfer (take-home)

ASP.NET Core Web API (.NET 8) + EF Core + SQL Server. React + TypeScript (Vite) front end.
Time-boxed to 3 hours. Smaller and correct beats bigger and rushed.
Out of scope: login, roles, styling, deployment.

## Layout

- src/StockTransfer.Api: API, EF Core, services
- tests/StockTransfer.Tests: xUnit against a REAL SQL Server test database (never EF InMemory)
- client/: Vite React TS, one screen

## How to work with me

- Plan first for anything touching dispatch, receive or idempotency. Wait for my approval before writing code.
- Smallest diff that solves the task. No refactors, new packages or extra features unless I ask.
- Comment only non-obvious logic (concurrency guards, SQL). I must be able to explain every line.
- Never read or edit bin/, obj/, node_modules/.
- Keep replies short.
- Do not automatically execute git operations (stage, commit, init or push) unless explicitly asked
- Do only the task I name. When it is done, stop and summarize what changed in a few lines. Never start the next task and never edit TASKS.md (I tick boxes and log time myself).

## Domain

- One Locations table: central warehouse + Colombo, Kandy, Galle. The warehouse can be a destination.
- 5 products. StockLevel(LocationId, ProductId, Quantity), unique on (LocationId, ProductId).
  Seed a row for EVERY location x product (0 if none) so receive is a plain UPDATE.
- Transfer(Id, FromLocationId, ToLocationId, Status, IdempotencyKey, timestamps)
  TransferLine(TransferId, ProductId, Quantity). Quantities are whole numbers.

## State machine (nothing else allowed)

- Create -> Requested (no stock effect)
- Requested -Dispatch-> Dispatched (sender -qty)
- Dispatched -Receive-> Received (receiver +qty)
- Requested -Cancel-> Cancelled (no stock effect)
  Goods in transit are in neither location. Total stock + in-transit is constant.

## Rules

- Stock never below 0: DB CHECK (Quantity >= 0) as backstop + conditional UPDATE.
- Requesting does NOT reserve stock. The UI stock figure is advisory; the dispatch check is authoritative.
- Same transfer moves stock once per step.

## Concurrency (core, one DB transaction each)

- Dispatch:
  1. UPDATE Transfers SET Status='Dispatched' WHERE Id=@id AND Status='Requested'
     0 rows -> reload: already Dispatched = 200 with current state; missing = 404; any other status = 409.
  2. Per line: UPDATE StockLevels SET Quantity = Quantity - @n
     WHERE LocationId=@from AND ProductId=@p AND Quantity >= @n
     0 rows -> roll back the whole transaction, 409 "Insufficient stock for <product>".
- Receive: same pattern, guard WHERE Status='Dispatched', then Quantity = Quantity + @n at the To location.
- Cancel: guard WHERE Status='Requested'.
- Use ExecuteUpdateAsync or raw SQL and check rows affected. Never read-then-write stock in C#.

## Idempotency

- POST /transfers accepts an Idempotency-Key header, stored in a nullable column with a unique filtered index (WHERE NOT NULL).
- Same key returns the existing transfer, not a new one. On a unique-violation race, catch it and return the existing.
- Repeating an already-applied action (dispatch, receive or cancel twice) = 200 with current state.
  Any other invalid transition (receive before dispatch, cancel after dispatch, dispatch after cancel) = 409.

## Validation and errors (ProblemDetails with a clear message)

- 400: quantity not an integer, <= 0, or > 1,000,000; From == To; no lines; duplicate product in one request; unknown location/product ID in the body
- 404: unknown ID in the route (transfer, branch)
- 409: insufficient stock, wrong status

## Seeding

Migrate then seed on startup. Must not duplicate data on restart.

## Tests (real SQL Server, unique DB per run)

Overstock dispatch (409, stock unchanged); double dispatch (moves once, 2nd = 200);
two concurrent dispatches via Task.WhenAll (moves once); receive before dispatch (409);
cancel after dispatch (409); receive adds to receiver; same Idempotency-Key twice (one transfer).

## Front end

One screen: pick from/to, add product + qty lines, show available stock (advisory), submit.
Generate the Idempotency-Key once per submit attempt (crypto.randomUUID()) and disable the button while in flight.
Transfers list with status filter and Dispatch/Receive/Cancel buttons. Show API error text inline. No styling.

## Out of scope unless time remains

Partial receive / damaged goods.
