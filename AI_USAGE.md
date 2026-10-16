# AI Usage

## Tools used

- Claude Code (VS Code extension), model: Sonnet 5.5
- Used for: scaffolding, schema and seed, create/receive/cancel/list endpoints, tests, React screen, explaining my own code
- Process: one task per session, plan mode first, I read every diff before committing (see TASKS.md and CLAUDE.md)

## Decisions I made myself

- **SQL Server in Docker, not LocalDB**
  - Claude's first T1 plan chose LocalDB because my prompt left the database blank
  - I rejected it before any code was written and required SQL Server 2022 in Docker
  - Why: LocalDB is Windows-only and the reviewer may use another operating system, Docker gives every reviewer the same setup, and tests can create and drop a uniquely named database on the same container
- **Added a DB CHECK on TransferLines.Quantity (T2)**
  - A review pass found the schema had none, so a negative quantity could bypass the dispatch guard
  - No issue found on T3
- **Plan first, one task per session, no git by AI**
  - Written into CLAUDE.md so Claude plans, stops after each task, and never commits
- **Bound SQL Server to localhost and kept the throwaway password in compose**
  - A reviewer's `docker compose up` must work with no extra setup, so the password stays in the repo, but the port is not exposed to the network
- **Accepted one new test package (Microsoft.AspNetCore.Mvc.Testing)**
  - CLAUDE.md says no new packages, so I approved this one explicitly in the T7 prompt
- **Kept the 7 tests from the brief for T7; listed coverage gaps in the README instead**
  - T10 later added a repeated receive and four partial-receive tests (12 in total)
  - Remaining gaps are listed in the README (Trade-offs)
- **Added a read-only GET /branches (T8)**
  - The form needs branch names and there was no endpoint for them
- **Used the Vite dev proxy instead of CORS (T8)**
  - The API stays unchanged
- **Reviewed the idempotency key lifecycle in the client (T8)**
  - It is kept on a network error or 5xx and dropped on success or 4xx, so a retry can't create a duplicate

## Where the AI was wrong or unhelpful

**T7 review pass: wrong claim about the idempotency test**

- The review said the idempotency test goes red if I drop the unique index or remove the replay lookup
- How I noticed: the T7 plan had already reasoned that the two layers cover each other, so removing either one alone leaves the test green
- What I changed: used the plan's breaks instead (set IdempotencyKey to null on insert = red; remove only the replay lookup = green by design) and ran them by hand

## What I wrote myself

- T5 dispatch transaction (AI explained and reviewed it)
- The business rules, edge cases and concurrency notes before any code: `Logic overview/business logic.txt` (my original notes) and `Logic overview/business logic revised.txt` (cleaned up, Claude's additions marked)
