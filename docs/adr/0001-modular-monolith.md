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

## Alternatives considered
- **Microservices from day one:** rejected, network, deployment and consistency cost without any measurement justifying it.
- **Single project, no module boundaries:** rejected, extraction of a service at stage 11 would require untangling shared code.
- **Schema-less shared tables with cross-module joins:** rejected, it makes later extraction of a module's data impossible.

## Evidence (stage 1)
- 9 architecture tests enforce the boundary; 18 integration tests run the whole host against a real PostgreSQL (Testcontainers).
- The baseline (see [journal](../journal/01-skeleton.md)) shows the monolith is not the bottleneck: on dataset S it serves the full
  ramp to 300 rps with p95 about 10 ms on one machine.

## Consequences
- A module can later be extracted into a service by moving its schema and replacing `IAccountsApi`-style
  interfaces with network clients.
- Cross-module consistency is handled in code (and later by outbox/queues), not by database constraints.
