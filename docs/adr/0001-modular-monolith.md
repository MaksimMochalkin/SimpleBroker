# ADR-0001: Modular monolith with a schema per module

**Status:** accepted (stage 1)

## Context
The project must grow through many stages (indexes, partitioning, matching, queues, Kubernetes) while staying
understandable for one developer. Microservices from day one would add network and deployment cost before
any measurement justifies it.

## Decision
- One deployable host, three modules: `Accounts`, `Orders`, `Trades`.
- Each module = implementation project + `*.Contracts` project. Other modules may reference only `*.Contracts`.
- One PostgreSQL database, one schema per module; no cross-schema foreign keys or joins.
- Module internals live in `SimpleBroker.<Module>.Internal` and are public only because EF Core tooling needs it;
  the boundary is enforced by `ModuleBoundaryTests`, not by `internal`.
- EF Core + Npgsql for writes; Dapper may be added later for hot read paths, after measurement.

## Consequences
- A module can later be extracted into a service by moving its schema and replacing `IAccountsApi`-style
  interfaces with network clients.
- Cross-module consistency is handled in code (and later by outbox/queues), not by database constraints.
