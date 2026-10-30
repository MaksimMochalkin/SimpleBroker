# ADR-0003: Concurrency control for account balances and transfers

**Status:** accepted (stage 2)

## Context

Withdrawals and transfers update shared account balances. A read-check-write sequence can overwrite a concurrent update or allow a withdrawal based on a stale balance. Transfers that lock accounts in opposite orders can deadlock. A single database isolation level is not equally suitable for every operation: a conditional update is enough for one-row withdrawals, while transfers need coordinated access to two rows.

## Decision

- Use `UPDATE ... WHERE available >= amount` for the default withdrawal path. The balance check and decrement happen in one database statement, so a stale application-side balance is never written.
- Use `SELECT ... FOR UPDATE` when the operation must read and make a decision before updating the row. This is the pessimistic path; concurrent writers wait for the row lock.
- Keep an optimistic withdrawal path using `Balance.Version` as an EF Core concurrency token. On a version conflict, retry a bounded number of times; return `409 Conflict` when contention remains after the retry budget.
- Provide `RepeatableRead` and `Serializable` withdrawal modes to demonstrate PostgreSQL transaction-level conflict detection. Retry SQLSTATE `40001` and `40P01` with bounded backoff; surface exhaustion as `409`, rather than silently dropping the operation.
- For transfers, lock account rows in ascending `account_id` order by default. This prevents a cycle caused solely by competing transfers acquiring the same pair of rows in reverse order.
- Keep intentionally unsafe `Naive` and `Unordered` modes behind `Accounts:AllowModeOverride`; they exist only for controlled experiments.

## Alternatives considered

- **Use Serializable for every write:** rejected. It provides stronger anomaly protection but adds abort/retry work and is unnecessary for a one-row conditional decrement.
- **Use optimistic concurrency for all updates:** rejected. It preserves correctness but turns contention into retries and possible client conflicts even when a short row lock or one SQL statement can complete the operation.
- **Use `xmin` as the concurrency token:** rejected. The API already exposes an explicit version, which is clearer for learning and tests.
- **Retry every database exception indefinitely:** rejected. Only known transient PostgreSQL states are retried, with a finite budget so requests cannot loop forever.

## Consequences

- Each endpoint selects the least complex mechanism that preserves its invariant; isolation is not a global switch.
- Pessimistic locking can increase tail latency when many clients target the same account.
- Optimistic and transaction-level concurrency can reject work with `409` after bounded retries. Callers must decide whether and when to retry.
- Ordered locking removes this particular transfer deadlock pattern, but does not prevent unrelated deadlocks elsewhere in the system.
- Benchmark results depend on account skew, concurrency, retry limits, and machine capacity. Correctness invariants are acceptance criteria; throughput and percentiles are measurements, not universal constants.

## Evidence

See the [stage 2 journal](../journal/02-accounts-transactions.md) and the [learning report](../journal/stage-02-learning-report-ru.md) for the C1/C2 runs, retry behavior, and measured trade-offs.
