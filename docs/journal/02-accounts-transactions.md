# Stage 2: accounts, transactions, deadlocks, and outbox

**Status:** in progress; latest Release build, 52 tests, live smoke checks and C1 passed. Ordered transfers and bounded-concurrency UnorderedRetry passed; UnorderedRetry still returns 409 under higher contention and one high-load Unordered sample had a 500. C3/L1′ are being measured; stage acceptance is not yet closed. See the [Russian learning report](stage-02-learning-report-ru.md) for current results.  
**Branch:** `stage/02-accounts-transactions` | **Target tag:** `v0.2` (not created)  
**Current verification:** 2026-10-28 | **Machine:** Intel Core i5-1135G7, 4 cores / 8 logical processors; PostgreSQL 17 in Docker; .NET 10.0.402; k6 2.2.0

> The sections below are the historical snapshot recorded on 2026-10-26. Their earlier Testcontainers failure and single-run C1/C2 observations were superseded by the 2026-10-28 verification and the linked learning report. Retained raw run files are in [`data/stage-02/`](data/stage-02/).

## Environment and evidence

Intel Core i5-1135G7 (4 cores / 8 logical processors), 15.7 GiB RAM, WDC PC SN530 NVMe; .NET SDK 10.0.402, Docker 29.9.2, k6 2.2.0, PostgreSQL 17.11. PostgreSQL has a 2 CPU / 4 GiB container limit. API, k6, and Docker run on the same machine, so results include host contention. Machine A was unavailable; Stage 2 specifies one machine for its measurements, so only B results are reported and no cross-machine comparison is made.

`dotnet build SimpleBroker.slnx -c Release -v q --nologo` succeeded with 0 errors and 5 analyzer warnings.

`dotnet test SimpleBroker.slnx --nologo -v q --filter "Category!=Demo"` reported 10 passing architecture tests and 42 failed integration tests. A focused rerun showed the failures occur before test bodies run: Testcontainers receives HTTP 500 from Docker Engine while starting the Resource Reaper, and fixture cleanup then throws `NullReferenceException`. A focused retry with `TESTCONTAINERS_RYUK_DISABLED=true` connected to Docker but stalled before fixture startup and was stopped without a result. Rerun the integration suite after resolving the container startup issue. Raw measurements are preserved under [`data/stage-02/`](data/stage-02/).

## C1 — concurrent withdrawals

Only one regular run per mode is present, so the values below are observations, not the planned three-run medians. With a 10,000 deposit and 5,000 withdrawals of 1:

| Mode | 200 | 409 | Final balance expected / actual | Lost | p95 | RPS |
|---|---:|---:|---:|---:|---:|---:|
| Naive | 5000 | 0 | 5000 / 9889 | 4889 | 311.3 ms | 474 |
| Atomic | 5000 | 0 | 5000 / 5000 | 0 | 295.8 ms | 513 |
| ForUpdate | 5000 | 0 | 5000 / 5000 | 0 | 1166.8 ms | 152 |
| Optimistic | 1269 | 3731 | 8731 / 8731 | 0 | 434.2 ms | 183 |
| RepeatableRead | 1981 | 3019 | 8019 / 8019 | 0 | 2456.4 ms | 39 |
| Serializable | 1762 | 3238 | 8238 / 8238 | 0 | 1795.5 ms | 45 |

The naive read-check-write sequence allowed concurrent requests to overwrite one another: 4,889 units were lost from the expected final balance. Atomic SQL and row locking preserved the invariant and completed all operations. Optimistic and serializable modes preserved the invariant by rejecting conflicts with 409; `RepeatableRead`/`Serializable` were slower in this sample because retries and serialization conflicts add work.

In the oversell variant (deposit 1,000; 5,000 attempts), Naive returned 5,000 successes and left 903, with `oversell=4000` and `lost=4903`. Atomic and ForUpdate returned exactly 1,000 successes and 4,000 insufficient-funds responses, leaving zero. The other safe modes had 1,000 successes, no oversell, and no lost balance; remaining attempts were rejected as conflicts or insufficient funds. Each mode has only one oversell run.

## C2 — opposing transfers

| Mode | Runs | Median RPS | Median p95 | Deadlock counts by run | Result |
|---|---:|---:|---:|---|---|
| Unordered | 3 | 8 | 21.13 s | 1901 / 1994 / 1967 | Each run preserved the 200,000 total. All produced 409 deadlock responses; runs 1 and 3 also contained 500 responses. |
| UnorderedRetry | 3 attempted | — | — | 912 / 2648 / 876 | Each harness process threw `TaskCanceledException` after `HttpClient.Timeout` reached 100 seconds; no completed client summary or total-balance assertion was captured. |
| Ordered | 3 | 183 | 841.5 ms | 0 / 0 / 0 | All 2,000 requests per run returned 200 and the total remained 200,000. |

Unordered locking reproduces the circular wait: transaction A holds one account and waits for the other while transaction B does the inverse. PostgreSQL detects the deadlock and aborts a transaction. Locking rows in a consistent order prevented deadlocks in all three recorded Ordered runs. UnorderedRetry cannot be counted as a passing remedy yet: database deadlock counts alone do not prove the client operations completed correctly.

## C3 — hot account

One k6 run per mode was saved; the wait-event snapshots were taken after the runs, not continuously during them.

| Mode | RPS | p50 | p95 | p99 | Expected statuses / failures | Additional evidence |
|---|---:|---:|---:|---:|---|---|
| Atomic | 497.45 | 28.1 ms | 629.06 ms | 1.11 s | 0/89561 HTTP failures | Up to 199 of 200 VUs |
| ForUpdate | 151.86 | 27.16 ms | 2.27 s | 4.12 s | 0/27351 HTTP failures | Post-run snapshot included 97 `Lock/tuple` waiters |
| Optimistic | 69.82 | 294.86 ms | 4.37 s | 5.02 s | 0/12573 HTTP failures | 7,344 conflicts (40.78/s); snapshot included 94 `MultiXactMemberSLRU` waits |
| Serializable | 34.76 | 227.51 ms | 12.63 s | 20.32 s | 109/6655 (1.63%) | 2,539 conflicts (13.26/s); 109 of 6,654 checks failed |

The single-row workload makes contention visible: atomic update gave the highest throughput in this run, while explicit locking and transaction-level conflict handling reduced throughput and increased tail latency. The snapshots are supporting clues, not synchronized measurements. The 109 unexpected Serializable responses require inspection before treating the mode as acceptable.

## L1′ — mixed load after Stage 2

| Run | RPS | p50 | p95 | p99 | HTTP failures | Dropped iterations | Outcome |
|---|---:|---:|---:|---:|---:|---:|---|
| 1 | 41.27 | 15.02 s | 15.51 s | 29.43 s | 49.88% | 63,636 | Failed both thresholds |
| 2 | 29.70 | 15.06 s | 15.56 s | 60 s | 87.89% | 68,481 | Failed both thresholds |
| 3 | 27.81 | 15.05 s | 15.46 s | 60 s | 97.10% | 69,277 | Failed both thresholds |

All runs reached the 500-VU ceiling, had visible request-timeout warnings, and missed the p95 < 1 s and HTTP-failure < 1% thresholds. The load-test common module already classifies 422 insufficient-funds responses as expected, so 422 alone does not account for the reported HTTP failure rate. The saved logs do not contain a reliable status breakdown for the failures. Machine B runs k6, the API, and PostgreSQL on one laptop, and the existing trades query is still deliberately untreated until Stage 3; the present evidence does not isolate how much each factor contributes. Do not label this as only an outbox or reservation cost.

## What went wrong / what to correct

1. **C1 was not repeated as required.** The runbook asks for three runs per mode but the directory contains only `run1` files for the normal case. Do not report these as medians or use their p95/RPS to rank modes conclusively.
2. **C2 Unordered had unexpected 500s.** The invariant still held, but the HTTP errors must be traced to API logs before the run can be called clean.
3. **C2 UnorderedRetry exceeded its harness timeout.** All saved attempts report the 100-second `HttpClient.Timeout`; the exception aborts the harness before it prints the operation totals. The exact server-side cause is not established by these files. Capture API/DB logs and test a smaller workload or bounded parallelism; avoid merely raising the timeout and calling it a pass.
4. **C3 has only one run per mode**, and the wait-event checks are post-run snapshots instead of samples captured during load. Repeat and capture a timestamped time series during the run.
5. **L1′ is a real failed result, not a measurement to discard.** Do not blame 422s: they are marked expected in the k6 callback. Investigate the observed timeouts, failed status classes, API/DB resource use, connection-pool waits, and query statistics. Keep the same machine and same data profile for any before/after comparison.
6. **Do not compare these values to machine A.** Machine A is unavailable, but Stage 2 requires a single consistent machine; the machine B results remain useful for internal comparisons. Stage 1 baseline data from A is not a valid direct reference for this run.

## Exit work still required

- Resolve the Docker/Testcontainers Resource Reaper startup failure, rerun the integration suite, and record the manual smoke checks for withdrawals, transfers, reservations, and outbox delivery.
- Repeat all C1 modes three times and record medians; retain each oversell sample.
- Diagnose the C2 500 responses and complete the `UnorderedRetry` runs with client totals and sum-of-balances verification.
- Repeat C3 and capture wait events during each load phase; explain the unexpected Serializable responses.
- Investigate why all L1′ runs fail thresholds; do not claim the reservation/outbox performance criterion passed.
- Complete the English ADR-0003/ADR-0004 and the remaining README/plan closure tasks. Only then decide whether to tag `v0.2` and commit the Stage 2 completion.
