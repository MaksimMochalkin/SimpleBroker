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
Dev machine A (machine A results): .NET SDK 10.0.402, Docker Desktop with 15.38 GiB RAM, Postgres container limited to 2 CPU / 4 GB,
k6 and the API on the same host as Docker. CPU model and disk: to be added.

Dev machine B (machine B results): Intel Core i5-1135G7 (4 cores / 8 logical processors), 15.7 GiB RAM, WDC PC SN530 NVMe SSD,
.NET SDK 10.0.402, Docker 29.9.2, k6 2.2.0, PostgreSQL 17.11. PostgreSQL container limited to 2 CPU / 4 GB;
k6, API, and Docker run on the same host. Results are comparable only within one machine.

## Data load (DataGen, seed 42)
| Scale | Phase times (s) | Total | DB size |
|---|---|---|---|
| S | orders 1.3, trades 0.2, accounts 0.1, balances 0.1, analyze 0.3 | 2.1 s | 77 MB |
| M | orders 7.1, trades 2.4, accounts 0.3, balances 0.3, analyze 1.6 | 11.8 s | 693 MB |

## Baseline (L1)
Machine A results:

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

Machine B results (same L1 scenario, M dataset; 100k accounts, 5M orders, 1M trades):

| Run | Actual RPS | p50 | p95 | p99 | HTTP failures | Dropped iterations |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 23.46 | 18.26 s | 37.54 s | 40.09 s | 0.20% (19) | 71,508 |
| 2 | 25.05 | 16.97 s | 34.99 s | 37.48 s | 0.00% | 70,849 |
| 3 | 24.44 | 17.57 s | 36.77 s | 38.33 s | 0.00% | 71,108 |
| **median M** | **24.44** | **17.57 s** | **36.77 s** | **38.33 s** | **0.00%** | **71,108 (about 88%)** |

The M data load was verified at 100,000 accounts, 5,000,000 orders, and 1,000,000 trades. Raw outputs:
`01-machineB-M-run-1.txt`, `01-machineB-M-run-2.txt`, and `01-machineB-M-run-3.txt`.

## Observations
- **Machine A S is served completely**: the achieved rate equals the target rate, p95 is about 10 ms, nothing is dropped.
  The first S run is slower (p95 18.8 ms, max 4 s): cold start and JIT warm-up.
- **Machine A M collapses**: only about 41 rps of the target 208 rps average is served, p95 is 22 s, 80% of planned iterations are dropped
  because all 500 k6 VUs are busy waiting. There are no errors: the system degrades in latency, it does not fail.
- Tenfold data growth cost a 5x throughput loss and a 2000x p95 increase. That is a query-plan signature, not a capacity one.
- **Machine A hypothesis, supported by machine B diagnostics below**: `GET /accounts/{id}/trades` filters by
  `buy_account_id = X OR sell_account_id = X` without an index and uses OFFSET, so calls scan the 1M-row table; with 50% of traffic
  and 2 Postgres CPUs this saturates PostgreSQL.
- Machine A runtime exception counters showed `NpgsqlException` and `EndOfStreamException` events in each run; their cause was not
  identified in the machine A logs.
- **Machine B M also misses the target badly**: median achieved throughput is 24.44 requests/s versus the scenario's average arrival target of about 208/s.
  Median p95 is 36.77 s against the 1 s threshold, and about 88% of scheduled iterations are dropped as k6 reaches its 500-VU ceiling.
  All three runs cross the latency threshold; run 1 also has 19 failed requests (0.20%), while runs 2 and 3 report no failed HTTP requests.
- **The machine B diagnostics support the trades-query bottleneck.** During runs 2 and 3, PostgreSQL repeatedly used about 190-210% CPU,
  effectively saturating its 2-CPU quota, while memory stayed around 1.06 GiB of 4 GiB. The end-of-series `pg_stat_statements` output
  lists two trades SELECT variants at about 7.65 s and 7.41 s mean execution time; order inserts averaged 4.0 ms and balance reads 1.2 ms.
  This strongly implicates the unindexed trades scan/OFFSET workload rather than insufficient PostgreSQL memory. Statement totals are cumulative
  since the last `pg_stat_statements_reset()` and should be interpreted accordingly.
- **Connection-pool exhaustion is an additional observed failure mode on machine B.** Runs 1 and 2 contain repeated Npgsql errors saying
  the 100-connection pool was exhausted and a 15-second wait timed out; run 1's 19 HTTP failures align with this. Slow queries hold pooled
  connections for a long time, so increasing the pool limit alone is not a safe fix: it may send even more concurrent work to an already
  CPU-saturated database.
- Run 3's runtime counters also recorded connection-reset, end-of-stream, and Npgsql exceptions despite reporting zero failed HTTP
  requests. These counters indicate runtime connection errors but do not by themselves establish how many distinct requests were affected.
- Machine B is slower than machine A on M, but these tests run k6, the API, and Docker on the same laptop, so contention is included in
  the measurement. The PostgreSQL CPU snapshots identify database saturation during observed samples; they do not isolate all host-level effects.

## Open items before tagging v0.1
- Group k6 URLs with a `name` tag: the k6 warning about more than 100k unique time series indicates unnecessary URL/account-ID cardinality
  in the load generator and can add measurement overhead.
- Re-run the baseline after the trades indexes/query plan are improved; keep machine A and machine B results in separate tables.

## Lessons
- Fail on measured symptoms first: the rule "add technology only after a measurement" paid off immediately, the M baseline
  gives stage 3 (EXPLAIN, indexes, keyset pagination) a concrete target: get M back to p95 < 1 s at 300 rps.
- Machine B's M run diagnostics confirm that the trades query is the dominant SQL cost and that long-running queries can exhaust
  the application's 100-connection Npgsql pool.
- Integration-test DDL must not use parameterized SQL (`ExecuteSqlAsync` with interpolation produces `$1`; use raw SQL with a constant).
- A local Postgres on 5432 silently hijacks the connection; the compose file publishes the container on 5433.

## Open questions for next stage
- Does `GET /accounts/{id}/trades` degrade with page depth and dataset size (L2)?
- Where is the write limit for `POST /orders` (L3)?
