# Stage 1: skeleton and baseline

**Branch:** `stage/01-skeleton` | **Tag:** `v0.1` (set after the p99 re-run, see "Open items")

## Context
- One deployable (modular monolith), PostgreSQL 17 in Docker (2 CPU, 4 GB), schema per module, primary keys only.
- Dataset S: 10k accounts, 500k orders, 100k trades. Dataset M: 100k accounts, 5M orders, 1M trades.
- Load: k6 scenario L1, ramping arrival rate 50 -> 300 rps over 6.5 min (average target rate 207.7 rps).
  Mix: 20% `POST /orders`, 50% `GET /accounts/{id}/trades?page=1..20&size=50`, 30% `GET /accounts/{id}/balance`.

## What was built
- Modules `Accounts`, `Orders`, `Trades` with `*.Contracts` and architecture tests (27 tests green: 9 architecture, 18 integration).
- EF Core migrations per module schema; `tools/ef.ps1` / `tools/ef.sh` apply them.
- OpenTelemetry -> Collector -> Prometheus / Jaeger -> Grafana.
- `tools/DataGen`: deterministic bulk loader (binary COPY, Zipf instruments, Pareto account activity).
- k6 scenarios L1-L4 in `loadtests/`.

## Environment
Dev machine A (these results): .NET SDK 10.0.402, Docker Desktop with 15.38 GiB RAM, Postgres container limited to 2 CPU / 4 GB,
k6 and the API on the same host as Docker. CPU model and disk: to be added. Results are comparable only within one machine.

## Data load (DataGen, seed 42)
| Scale | Phase times (s) | Total | DB size |
|---|---|---|---|
| S | orders 1.3, trades 0.2, accounts 0.1, balances 0.1, analyze 0.3 | 2.1 s | 77 MB |
| M | orders 7.1, trades 2.4, accounts 0.3, balances 0.3, analyze 1.6 | 11.8 s | 693 MB |

## Baseline (L1)
| Run | Scale | Actual RPS | p50 | p95 | p99 | Errors | Dropped iterations |
|---|---|---|---|---|---|---|---|
| 1 | S | 207.3 | 6.15 ms | 18.77 ms | n/a | 0.00% | 151 |
| 2 | S | 207.7 | 5.59 ms | 10.40 ms | n/a | 0.00% | 0 |
| 3 | S | 207.7 | 5.07 ms | 10.06 ms | n/a | 0.00% | 0 |
| **median S** | S | **207.7** | **5.59 ms** | **10.40 ms** | n/a | 0.00% | |
| 4 | M | 40.8 | 9.64 s | 22.69 s | n/a | 0.00% | 64,812 (80%) |
| 5 | M | 41.1 | 9.67 s | 22.57 s | n/a | 0.00% | 64,672 (80%) |
| 6 | M | 42.4 | 9.28 s | 21.99 s | n/a | 0.00% | 64,188 (79%) |
| **median M** | M | **41.1** | **9.64 s** | **22.57 s** | n/a | 0.00% | |

Raw k6 output is kept in `docs/journal/data/`. p99 was not printed by the default k6 summary; the scripts now set
`summaryTrendStats` to include `p(99)`.

## Observations
- **S is served completely**: the achieved rate equals the target rate, p95 is about 10 ms, nothing is dropped.
  The first S run is slower (p95 18.8 ms, max 4 s): cold start and JIT warm-up.
- **M collapses**: only about 41 rps of the target 208 rps average is served, p95 is 22 s, 80% of planned iterations are dropped
  because all 500 k6 VUs are busy waiting. There are no errors: the system degrades in latency, it does not fail.
- Tenfold data growth cost a 5x throughput loss and a 2000x p95 increase. That is a query-plan signature, not a capacity one.
- **Hypothesis (not proven yet)**: `GET /accounts/{id}/trades` filters by `buy_account_id = X OR sell_account_id = X`
  without an index and uses OFFSET, so each call scans the 1M-row table; with 50% of traffic and 2 Postgres CPUs this saturates Postgres.
  Missing evidence: CPU of app and Postgres under load, `pg_stat_statements` top queries.
- Runtime exception counters show `NpgsqlException` = 2 x `EndOfStreamException` in every run. Cause unknown, to be checked
  against the API and Postgres logs.

## Open items before tagging v0.1
- Re-run L1 with p99 enabled (on a second, weaker laptop; record its environment separately and do not mix with machine A).
- During an M run capture `docker stats` and the `pg_stat_statements` top 5 to confirm or reject the hypothesis.
- Group k6 URLs with a `name` tag: the k6 warning "100k unique time series" means the load generator itself carries extra overhead.

## Lessons
- Fail on measured symptoms first: the rule "add technology only after a measurement" paid off immediately, the M baseline
  gives stage 3 (EXPLAIN, indexes, keyset pagination) a concrete target: get M back to p95 < 1 s at 300 rps.
- Integration-test DDL must not use parameterized SQL (`ExecuteSqlAsync` with interpolation produces `$1`; use raw SQL with a constant).
- A local Postgres on 5432 silently hijacks the connection; the compose file publishes the container on 5433.

## Open questions for next stage
- Does `GET /accounts/{id}/trades` degrade with page depth and dataset size (L2)?
- Where is the write limit for `POST /orders` (L3)?
