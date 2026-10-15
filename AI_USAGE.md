# AI Usage

## Tools used

- Claude Code (VS Code extension), model: Sonnet 5.5
- Used for: scaffolding, schema and seed, create/receive/cancel/list endpoints, tests, React screen, explaining my own code
- Process: one task per session, plan mode first, I read every diff before committing (see TASKS.md and CLAUDE.md)

## Decisions I made myself

- **SQL Server in Docker, not LocalDB**
  - Claude's first T1 plan chose LocalDB because my prompt left the database blank
  - I rejected it before any code was written and required SQL Server 2022 in Docker
  - Why is that: LocalDB is Windows-only and I am not sure if the reviewer is not using other operating systesm, Docker gives every reviewer the same setup, and tests can create and drop a uniquely named database on the same container
- **Plan first, one task per session, no git by AI**
  - Written into CLAUDE.md so Claude plans, stops after each task, and never commits

## Where the AI was wrong or unhelpful

- <Task>: <what the AI did wrong>
  - How I noticed: <...> -- Fill for indivudal tasks if something went wrong
  - What I changed: <...>

## What I wrote myself

- T5 dispatch transaction (AI explained and reviewed it) - (business logic.txt and updated business logic.txt)
