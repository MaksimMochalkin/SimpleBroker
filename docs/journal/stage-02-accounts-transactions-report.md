# Stage 2 report: accounts, transactions, deadlocks, and outbox

**Date of recorded results:** 2026-10-26  
**Status:** in progress; test and load evidence has been recorded, but Stage 2 exit criteria are not all met.  
**Machine:** B only | **Branch:** `stage/02-accounts-transactions` | **Target tag:** `v0.2` (not created)

## 1. Purpose and scope

Stage 2 adds concurrency-safe balance operations and transactional events to the broker. The learning goals are to reproduce lost updates and deadlocks, compare safe withdrawal strategies, prevent a Buy order from spending more than the account can afford, and deliver domain events through an outbox. The stage intentionally does not solve the slow trades query, add sell-position reservations, implement order cancellation, or connect a real message broker; those belong to later stages.

The Stage 2 plan permits all measurements to run on one machine. Machine A was unavailable, so the Stage 2 results here use machine B only. This is not a blocker for internal Stage 2 comparisons. It does mean the numbers must not be mixed with machine A's Stage 1 baseline.

## 2. Environment and verification evidence

Machine B: Intel Core i5-1135G7 (4 cores / 8 logical processors), 15.7 GiB RAM, WDC PC SN530 NVMe SSD; .NET SDK 10.0.402, Docker 29.9.2, k6 2.2.0, PostgreSQL 17.11. PostgreSQL ran with a 2 CPU / 4 GiB container limit. The API, k6, and Docker all ran on the same laptop, so the host itself was also under load.

`dotnet build SimpleBroker.slnx -c Release -v q --nologo` succeeded with 0 errors and 5 analyzer warnings.

The non-demo test command was:

```powershell
dotnet test SimpleBroker.slnx --nologo -v q --filter "Category!=Demo"
```

The architecture test project completed with 10 passed and 0 failed. The non-demo integration project completed with 0 passed and 42 failed. A focused rerun showed that the failures occur before test bodies execute: Testcontainers receives HTTP 500 from Docker Engine while starting its Resource Reaper container, then fixture cleanup throws `NullReferenceException`. A retry with `TESTCONTAINERS_RYUK_DISABLED=true` connected to Docker but stalled before fixture startup and was stopped without a result. This is an environment/setup failure, not proof that the application assertions failed; the integration suite remains unverified. The raw test and load logs should be kept with the project evidence.

## 3. What changed in the design

The implementation provides six withdrawal modes: a deliberately unsafe read-check-write `Naive` example, an atomic conditional SQL update, row locking with `FOR UPDATE`, optimistic version checking, `RepeatableRead`, and `Serializable`. Transactions retry selected PostgreSQL serialization/deadlock errors, with a finite retry limit. Transfers support the intentionally unordered lock acquisition used to demonstrate deadlocks, ordered locking, and retry after an unordered deadlock.

Buy orders reserve `price × quantity` through the Accounts contract before the Orders row is inserted. Buy Market orders need a price ceiling. A sell order does not reserve funds because positions are out of scope. Reservation IDs make reserve/release requests idempotent. Accounts and Orders each write their own outbox row in their local transaction; a background publisher claims pending messages with `SKIP LOCKED` and sends them to a logging sink. This is at-least-once delivery, not exactly-once delivery.

The implementation also adds nonnegative balance constraints and metrics for retries, conflicts, and outbox lag. The point of the experiments is to compare correctness and contention costs, not to assume the most sophisticated isolation level is automatically best.

### 3.1 Step-by-step execution record

This distinguishes the planned runbook workflow from the steps for which saved evidence exists:

1. **Stage branch and code:** the worktree is on `stage/02-accounts-transactions`, and the Stage 2 source and runbook are present. The run history does not prove the original checkout/tag verification steps, so those are not described as completed.
2. **Build and automated tests:** the Release solution build succeeded with 0 errors and 5 analyzer warnings. The architecture tests passed 10/10. The 42 integration tests failed during shared fixture initialization because Docker returned HTTP 500 when Testcontainers started its Resource Reaper; test assertions did not run. A focused attempt with the reaper disabled stalled before fixture initialization completed.
3. **Database migration and data setup:** the runbook asks to create/apply the Accounts and Orders `Stage02` migrations, inspect the schema, and reload scale-S data. The saved C1/C2/C3/L1 files do not include those command results. Do not infer that the manual migration/schema checks passed from the load logs alone.
4. **API smoke tests:** withdrawal, transfer, reservation, and outbox delivery checks are specified in runbook E1–E5. There are no saved responses or query results for those checks, so their manual completion is unverified.
5. **C1 withdrawals:** the saved contention logs confirm lost updates and overselling in `Naive` and correct invariants in the safe-mode samples; the required three normal repetitions are missing.
6. **C2 transfers:** `Unordered` and `Ordered` have three result files each. `UnorderedRetry` has three timeout traces but no completed client summaries.
7. **C3 and L1′:** the k6 files contain one run per C3 mode and three L1′ runs. C3 has one post-run wait snapshot per mode. L1′ exceeded both thresholds in all three runs.
8. **Outbox/schema and stage closure:** outbox-related implementation is present, but integration tests did not run and no standalone smoke evidence was saved. English ADR-0003/ADR-0004, remaining plan closure, the `v0.2` tag, and the completion commit are not recorded as done.

## 4. C1 — reproduce lost updates

The normal test used one account with 10,000 units and 5,000 concurrent withdrawals of 1 unit. In the recorded Naive run, all 5,000 requests returned success, but the final balance was 9,889 instead of 5,000. The measured `lost=4889` is the direct evidence of lost updates: multiple requests read the same earlier balance and then overwrite one another's writes.

Atomic SQL completed all requests and left exactly 5,000. `ForUpdate` did the same, at a lower throughput and higher p95 in this single sample. Optimistic checking and the two isolation-level modes rejected a portion of requests with 409 responses but left the balance matching the number of accepted withdrawals. Those responses are not lost money: they are explicit conflicts after retries were exhausted.

| Mode | Successes | 409s | Expected balance | Actual balance | Lost | p95 | RPS |
|---|---:|---:|---:|---:|---:|---:|---:|
| Naive | 5000 | 0 | 5000 | 9889 | 4889 | 311.3 ms | 474 |
| Atomic | 5000 | 0 | 5000 | 5000 | 0 | 295.8 ms | 513 |
| ForUpdate | 5000 | 0 | 5000 | 5000 | 0 | 1166.8 ms | 152 |
| Optimistic | 1269 | 3731 | 8731 | 8731 | 0 | 434.2 ms | 183 |
| RepeatableRead | 1981 | 3019 | 8019 | 8019 | 0 | 2456.4 ms | 39 |
| Serializable | 1762 | 3238 | 8238 | 8238 | 0 | 1795.5 ms | 45 |

### Oversell variant

The oversell variant started with only 1,000 units and attempted 5,000 withdrawals. Naive allowed all 5,000 and returned `oversell=4000`; the remaining balance of 903 and `lost=4903` demonstrate how overlapping writes can make successful sales exceed the original funds. Atomic and ForUpdate each accepted exactly 1,000 and rejected the other 4,000 with 422. Optimistic and isolation-level modes also kept `lost=0` and `oversell=0`; conflicts accounted for some requests, and insufficient funds rejected the rest.

These results show the key trade-off: a conditional single-row update is a small and effective correctness boundary for a simple withdrawal. Locks and stronger isolation are useful when a transaction needs read/modify/write logic, but they introduce queueing, retries, and user-visible conflict responses.

### Sampling limitation

The Stage 2 runbook requires three runs per ordinary C1 mode and a median. The saved evidence has only one normal run per mode. The table above is explicitly a single observed run, not a median or a robust performance ranking. The oversell logs also contain one run per mode.

## 5. C2 — reproduce and prevent deadlocks

The C2 workload performs opposing transfers between two accounts. In `Unordered`, two transactions can each hold a different account row while waiting for the row held by the other transaction. PostgreSQL detects the cycle after its deadlock timeout and aborts one participant. The recorded runs show many 409 deadlock responses and a nonzero database deadlock count. All three runs nevertheless preserved the total balance of 200,000. Runs 1 and 3 also returned unexpected 500 responses, which are not acceptable as a clean result and need an API-log investigation.

`Ordered` acquired locks in a consistent order. All three saved runs returned 2,000 HTTP 200 responses, preserved the 200,000 total, and reported zero deadlocks:

| Mode | Runs | Median RPS | Median p95 | Deadlocks by run | Outcome |
|---|---:|---:|---:|---|---|
| Unordered | 3 | 8 | 21.13 s | 1901 / 1994 / 1967 | 409s in all runs; 500s in runs 1 and 3; total conserved |
| Ordered | 3 | 183 | 841.5 ms | 0 / 0 / 0 | 2,000 successes/run; total conserved |
| UnorderedRetry | 3 attempts | — | — | 912 / 2648 / 876 | No completed harness summary |

All three UnorderedRetry output files end with `TaskCanceledException` after the configured `HttpClient.Timeout` of 100 seconds. The harness exits before reporting response codes or final balance totals. The deadlock counter values do not prove that the retried requests completed. The exact cause of the long run is not established in these files; investigate the API and database logs, then repeat with a bounded workload and capture the client-side totals.

## 6. C3 — hot-account contention

The hot-account k6 profile directs concurrent withdrawals to one account while virtual users rise to 200. One run per mode was saved. Atomic update handled the highest rate in this sample. Explicit row locking reduced throughput and had a post-run snapshot with 97 sessions waiting on tuple locks. Optimistic mode exposed 7,344 conflicts, and its snapshot included 94 `MultiXactMemberSLRU` waits. Serializable was the slowest of the four and had 109 failed checks, which means it emitted responses outside the expected 200-or-409 set; those responses need investigation.

| Mode | RPS | p50 | p95 | p99 | HTTP failures | Notes |
|---|---:|---:|---:|---:|---:|---|
| Atomic | 497.45 | 28.1 ms | 629.06 ms | 1.11 s | 0/89561 | 0 failed checks |
| ForUpdate | 151.86 | 27.16 ms | 2.27 s | 4.12 s | 0/27351 | Post-run snapshot: 97 tuple lock waiters |
| Optimistic | 69.82 | 294.86 ms | 4.37 s | 5.02 s | 0/12573 | 7,344 conflicts; 40.78 per second |
| Serializable | 34.76 | 227.51 ms | 12.63 s | 20.32 s | 109/6655 (1.63%) | 2,539 conflicts; 109 checks failed |

The wait queries were captured after a run, rather than as a time series while the load was active. They are suggestive but cannot establish how long each wait condition persisted or correlate it precisely with latency. Repeat the tests and sample `pg_stat_activity` during each phase.

## 7. L1′ — mixed load with reservations and outbox

The Stage 1 L1 profile combines order writes, trades reads, and balance reads. All three Stage 2 L1′ runs missed the `p95 < 1s` and `http_req_failed < 1%` thresholds:

| Run | RPS | p50 | p95 | p99 | HTTP failures | Dropped iterations |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 41.27 | 15.02 s | 15.51 s | 29.43 s | 49.88% | 63,636 |
| 2 | 29.70 | 15.06 s | 15.56 s | 60 s | 87.89% | 68,481 |
| 3 | 27.81 | 15.05 s | 15.46 s | 60 s | 97.10% | 69,277 |

All reached the 500-VU ceiling and the output includes request timeouts. The k6 common module already marks 422 insufficient-funds responses as expected for `http_req_failed`; 422 responses alone therefore cannot explain the reported failures. The raw summaries do not provide a reliable status breakdown for the unsuccessful requests. Do not characterize this as only the cost of outbox or reservations.

Several factors could contribute: the trades query is intentionally still unoptimized until Stage 3, the write path now includes a reservation transaction and outbox write, and k6/API/PostgreSQL share one laptop. The existing logs do not isolate those costs. Capture database statement statistics, connection-pool waits, API exceptions, and HTTP status counts in a controlled rerun. The failed measurements remain valid evidence and must not be discarded.

## 8. What was done incorrectly and what the errors mean

1. **The prescribed C1 repetitions were not completed.** Only `run1` exists for each normal C1 mode; the project plan expected three and a median. Performance comparisons from these files are provisional.
2. **Unordered C2 was not fully clean.** Deadlocks are expected for this demonstration, but the additional 500s in two runs are not. The balance invariant passing does not excuse unexplained server errors.
3. **The retry harness timed out before measuring completion.** The saved error is a 100-second client timeout, not evidence that the balance logic is incorrect or correct. It makes the experiment incomplete; it does not reveal the root server-side cause.
4. **C3's wait-event sampling was mistimed.** A post-run snapshot cannot substitute for the runbook's requested observation during load. The recorded 97/94 counts need a synchronized repeat.
5. **The L1′ failure is not an expected 422 accounting issue.** The k6 response callback already excludes 422 from `http_req_failed`. The warnings show request timeouts, while the saved files do not identify the status codes behind all failures. Determine the actual mix before drawing a cause.
6. **Integration tests did not reach their assertions.** All 42 failed because Testcontainers could not start the Docker Resource Reaper after Docker Engine returned HTTP 500; cleanup then produced a null-reference error. Resolve the Testcontainers/Docker startup issue and rerun before claiming integration coverage. A retry with the Resource Reaper disabled connected to Docker but stalled before fixture startup and was stopped without a result.
7. **Machine A is not required for Stage 2 validation.** The plan says to use one machine for these experiments. Keep the data on B and avoid a direct comparison to A.

## 9. Required follow-up before Stage 2 closure

- Resolve Docker/Testcontainers Resource Reaper startup, rerun the integration suite, and record manual smoke checks, including outbox publication and reservation state.
- Run three ordinary C1 samples per mode and calculate medians.
- Investigate the two Unordered runs' 500 responses.
- Fix or explain the 100-second UnorderedRetry run timeout, then verify HTTP results and the invariant from completed client output.
- Repeat C3 and collect wait-event/resource samples during the load; investigate the Serializable unexpected responses.
- Diagnose L1′ timeouts and failures, and do not claim its acceptance criteria pass unless the thresholds actually pass.
- Complete ADR-0003 and ADR-0004 and close the remaining Stage 2 checklist. Only then decide to create tag `v0.2` and make a Stage 2 completion commit.

## 10. Raw data

All source k6 and contention output files are in [`data/stage-02/`](data/stage-02/). The plan with the populated result tables is [Stage 2](../../plans/stage-02-accounts-transactions.md), and the Windows execution procedure is the [Stage 2 runbook](../RUNBOOK-stage-02.md).
