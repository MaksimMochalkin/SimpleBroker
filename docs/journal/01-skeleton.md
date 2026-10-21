# Stage 1: skeleton and baseline

**Branch:** `stage/01-skeleton` | **Tag:** `v0.1` (not set until baseline numbers are recorded)

## Goal
A working modular monolith with full observability and a reproducible baseline on dataset sizes S and M.

## What was built
- Modules `Accounts`, `Orders`, `Trades` with contracts and architecture tests.
- PostgreSQL schema per module; EF Core migrations; primary keys only (no secondary indexes yet).
- OpenTelemetry -> Collector -> Prometheus / Jaeger -> Grafana.
- `tools/DataGen`: deterministic bulk loader (binary COPY, Zipf instruments, 80/20 account activity).
- k6 scenarios L1-L4 in `loadtests/`.

## Environment
CPU / RAM / disk / OS / Docker limits / .NET and Postgres versions: _to be filled in._

## Data load
| Scale | Phase times | DB size | Notes |
|---|---|---|---|
| S | | | |
| M | | | |

## Baseline (L1, ramping 50 -> 300 rps)
| Run | Scale | Target RPS | Actual RPS | p50 | p95 | p99 | Errors | CPU app / PG | What saturated first |
|---|---|---|---|---|---|---|---|---|---|
| 1 | S | | | | | | | | |
| 2 | S | | | | | | | | |
| 3 | M | | | | | | | | |

## Observations
_To be written after the runs: where the knee is, which endpoint is slowest, what the dashboards show._

## Open questions for next stage
- Does `GET /accounts/{id}/trades` degrade with page depth and dataset size (L2)?
- Where is the write limit for `POST /orders` (L3)?
