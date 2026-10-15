# TASKS

Working rule: one task = one Claude Code session.
Start each session with:
"Read CLAUDE.md and TASKS.md. Do T<n> only. Plan first and wait for my OK."
After each task: run it, read the whole diff, commit, tick the box, log the time.
Budget: about 160 min of work (limit is 3 h). Reading AI output counts as time spent.

## Branches (merge to main with --no-ff, commit per task inside)
- main: T0
- feat/backend-foundation: T1-T3
- feat/transfer-workflow: T4-T7
- feat/frontend: T8
- docs/readme: T9-T10

---

## T0 Setup (main, 10 min)
- [ ] git init; .gitignore (`dotnet new gitignore`, add node_modules/)
- [ ] CLAUDE.md, .claude/settings.json (deny bin/obj/node_modules), TASKS.md
- [ ] README.md and AI_USAGE.md stubs
Done when: first commit on main.

## T1 Scaffold (10 min)
- [ ] .NET 8 Web API project, xUnit test project, solution
- [ ] EF Core SqlServer packages, connection string in appsettings
Done when: `dotnet build` passes and the API starts.
Be able to explain: DbContext lifetime, where the connection string lives.

## T2 Schema and seed (15 min)
- [ ] Entities: Location, Product, StockLevel, Transfer, TransferLine
- [ ] Unique (LocationId, ProductId); CHECK (Quantity >= 0)
- [ ] Filtered unique index on Transfer.IdempotencyKey (WHERE NOT NULL)
- [ ] Migration; Migrate() + idempotent seed on startup
      (4 locations, 5 products, a stock row for every location x product, some non-zero)
Done when: DB is created on first run, a restart does not duplicate data,
and the CHECK constraint is visible in SSMS.
Be able to explain: why a CHECK AND a conditional UPDATE.

## T3 Stock endpoint (5 min)
- [ ] GET /branches/{id}/stock (404 for unknown id)
Done when: returns all 5 products for each location.

## T4 Create transfer (20 min)
- [ ] POST /transfers: validation (see CLAUDE.md), Idempotency-Key header
- [ ] ProblemDetails error responses
Done when: every 400 case returns a clear message, and the same key twice
creates exactly one row.
Be able to explain: filtered index, the unique-violation race handling.

## T5 Dispatch (20 min) - write this one yourself, AI explains
- [ ] One transaction: status guard UPDATE, then conditional stock UPDATE per line
- [ ] 0 rows on guard: 200 if already Dispatched, 404 if missing, else 409
- [ ] 0 rows on stock: rollback everything, 409 with product name
Done when (check in SQL after each): dispatch twice moves stock once; an overstock
dispatch returns 409 and leaves stock and status unchanged.
Be able to explain: every line, and why rollback also reverts the status change.

## T6 Receive, cancel, list (15 min)
- [ ] POST /transfers/{id}/receive and /cancel (same guard pattern as T5)
- [ ] GET /transfers?branchId=&status=
Done when: receive before dispatch and cancel after dispatch return 409.

## T7 Tests (25 min)
- [ ] Real SQL Server test DB, unique name per run, cleaned up after
- [ ] The 7 tests listed in CLAUDE.md
Done when: all green AND each test goes red if you temporarily break its guard
(for example remove `Quantity >= @n`). This proves the tests check what matters.

## T8 React screen (30 min)
- [ ] Vite + React + TS in client/; CORS or dev proxy
- [ ] Request form: from/to, product + qty lines, advisory stock, idempotency key
- [ ] Transfers list with filter and action buttons; API errors shown inline
Done when: a full request, dispatch and receive works in the browser, and a
double-click creates one transfer.

## T9 Docs (10 min)
- [ ] README: run steps, assumptions, trade-offs, "with another day"
- [ ] AI_USAGE.md: tools and models used, one real AI mistake (from the log
      below), what you wrote or decided yourself
- [ ] Time spent

## T10 Optional rehearsal (10 min)
- [ ] Think through partial receive (ReceivedQuantity on the line, a difference
      record). Do NOT build it; be ready to do it live.

---

## Time log
| Task | Est | Actual |
|------|-----|--------|
| T0 | 10 | |
| T1 | 10 | |
| T2 | 15 | |
| T3 | 5 | |
| T4 | 20 | |
| T5 | 20 | |
| T6 | 15 | |
| T7 | 25 | |
| T8 | 30 | |
| T9 | 10 | |

## AI mistakes log (feeds AI_USAGE.md)
| Task | What the AI did wrong | How I noticed | What I changed |
|------|----------------------|---------------|----------------|