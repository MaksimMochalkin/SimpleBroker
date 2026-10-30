# ADR-0005: Indexed keyset pagination for trade history

**Status:** accepted (stage 3)

## Context

The trade-history endpoint must serve a hot account's history from a `trades` table that may contain 50–100 million rows. The original query combined buyer and seller predicates with `OR` and used offset pagination, which led to large scans and increasing work for later pages.

The stage also compared EF Core with Dapper and investigated whether the Npgsql connection pool or PostgreSQL resource limits constrained throughput. Measurements were made on one Windows machine with the API, k6, and Docker sharing host resources, so the results are environment-specific.

## Decision

- Keep EF Core as the primary trade-history endpoint.
- Query buyer and seller histories as separate branches, combine them with `UNION ALL`, exclude duplicate self-trades from the seller branch, and order by `(executed_at DESC, id DESC)`.
- Add composite indexes on `(buy_account_id, executed_at DESC, id DESC)` and `(sell_account_id, executed_at DESC, id DESC)`.
- Use cursor-based keyset pagination on `(executed_at, id)` instead of deep `OFFSET`; request `size + 1` rows to determine whether a next cursor exists.
- Keep the Dapper implementation as an opt-in benchmark endpoint, disabled by default. It must remain contract-equivalent to EF Core and is not selected as the production endpoint based on the available measurements.
- Size PostgreSQL CPU resources for the target workload and verify the actual container limits. In this environment, increasing the PostgreSQL CPU limit from 2 to 4 CPUs materially improved repeatability at 100M rows; the benchmark does not establish a universal production sizing recommendation.

## Alternatives considered

- **A single `OR` predicate with one general index:** rejected for the hot path; separate account-leading indexes provide direct access to each side of the history.
- **Deep offset pagination:** rejected because the database must walk past rows before returning a later page, and the work grows with page depth.
- **Replace EF Core with Dapper by default:** deferred. Dapper showed an advantage in some 50M page-50 runs, but at 100M and 200 RPS both implementations performed similarly after increasing PostgreSQL resources. The evidence is insufficient to justify changing the primary endpoint.
- **Increase only `Maximum Pool Size`:** rejected as an unproven remedy. Pool pending requests remained zero in sampled runs, and the available evidence pointed more strongly to resource pressure at higher throughput.

## Evidence (stage 3)

- On 50M rows, an `OFFSET 2450` query took about 14.6 seconds without secondary indexes and about 284 ms with indexes. On 100M, the corresponding measurements were about 8.64 seconds and 1.10 seconds. These were not controlled for cache state.
- On 100M rows with 4 PostgreSQL CPUs, both EF Core and Dapper served the page-50 profile at approximately 200 RPS with p95 around 9–13 ms and no failed checks.
- With 8 PostgreSQL CPUs, EF Core at a 600 RPS target reached about 488 RPS, p95 about 1.08 seconds, and saturated the configured 400 VUs.
- With 12 PostgreSQL CPUs and 800 max VUs, EF Core reached about 582 RPS with no failed HTTP checks, but p95 was about 1.28 seconds and 2,360 iterations were dropped. The 600 RPS target and the plan's low-latency target were not met.
- Sampled Npgsql pending requests were zero in the observed diagnostic windows. This does not rule out short pool waits in unsampled intervals or prove that the pool cannot constrain other workloads.
- Detailed environment, measurements, limitations, and raw result paths are recorded in the [stage 3 report](../journal/data/reports/stage-03-trades-explain-report-ru.md).

## Consequences

- Later pages have a stable ordering and avoid work proportional to the requested offset.
- The two composite indexes increase storage and write amplification; the measured 100M dataset occupied roughly 9.6 GiB of table heap and 9.9 GiB of indexes.
- EF Core remains the supported primary implementation; Dapper can be reconsidered if a controlled benchmark demonstrates a repeatable advantage at the intended production workload.
- The stage is recorded as reviewed and closed by agreement, with the explicit caveat that 600 RPS at the target latency was not achieved. The observed 582 RPS run had high tail latency and dropped iterations, and must not be presented as meeting that SLO.
