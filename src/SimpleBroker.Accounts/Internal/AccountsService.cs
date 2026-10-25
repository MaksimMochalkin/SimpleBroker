namespace SimpleBroker.Accounts.Internal;

using System.Data;
using System.Diagnostics;

using Microsoft.EntityFrameworkCore;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Platform;
using SimpleBroker.Platform.Outbox;

public sealed class AccountsService(AccountsDbContext db) : IAccountsApi
{
    public async Task<bool> ExistsAsync(long accountId, CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId, ct);

    public async Task<long> CreateAsync(string owner, string currency, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var account = new Account { Owner = owner, Currency = currency, CreatedAt = DateTime.UtcNow };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);

        db.Balances.Add(new Balance { AccountId = account.Id });
        await db.SaveChangesAsync(ct);

        await tx.CommitAsync(ct);
        return account.Id;
    }

    /// <summary>Atomic increment in SQL; no read-modify-write race. Returns null if the account has no balance row.</summary>
    public async Task<BalanceResponse?> DepositAsync(long accountId, decimal amount, CancellationToken ct)
    {
        var rows = await db.Balances
            .Where(b => b.AccountId == accountId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Available, b => b.Available + amount)
                .SetProperty(b => b.Version, b => b.Version + 1), ct);

        return rows == 0 ? null : await GetBalanceAsync(accountId, ct);
    }

    public Task<BalanceResponse?> GetBalanceAsync(long accountId, CancellationToken ct) =>
        db.Balances.AsNoTracking()
            .Where(b => b.AccountId == accountId)
            .Select(b => new BalanceResponse(b.AccountId, b.Available, b.Reserved, b.Version))
            .FirstOrDefaultAsync(ct);

    // ---------------------------------------------------------------- withdraw (case C1)

    public async Task<WithdrawResult> WithdrawAsync(long id, decimal amount, WithdrawMode mode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        WithdrawOutcome? outcome = null;
        try
        {
            outcome = mode switch
            {
                WithdrawMode.Naive => await NaiveAsync(id, amount, ct),
                WithdrawMode.Atomic => await AtomicAsync(id, amount, ct),
                WithdrawMode.ForUpdate => await TxAsync(id, amount, IsolationLevel.ReadCommitted, true, ct),
                WithdrawMode.RepeatableRead => await TxAsync(id, amount, IsolationLevel.RepeatableRead, false, ct),
                WithdrawMode.Serializable => await TxAsync(id, amount, IsolationLevel.Serializable, false, ct),
                WithdrawMode.Optimistic => await OptimisticAsync(id, amount, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
        }
        finally
        {
            AccountsMetrics.Withdraw.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("outcome", outcome?.ToString() ?? "Error"));
        }

        return new WithdrawResult(outcome.Value, outcome == WithdrawOutcome.Ok ? await GetBalanceAsync(id, ct) : null);
    }

    // DELIBERATE BUG: read, check, then write an absolute value computed from a stale read (lost update, oversell).
    private async Task<WithdrawOutcome> NaiveAsync(long id, decimal amount, CancellationToken ct)
    {
        var b = await db.Balances.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == id, ct);
        if (b is null) return WithdrawOutcome.NotFound;
        if (b.Available < amount) return WithdrawOutcome.Insufficient;

        await db.Balances.Where(x => x.AccountId == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Available, b.Available - amount)
            .SetProperty(x => x.Version, b.Version + 1), ct);
        return WithdrawOutcome.Ok;
    }

    // One statement: check and decrement happen under the row lock taken by UPDATE.
    private async Task<WithdrawOutcome> AtomicAsync(long id, decimal amount, CancellationToken ct)
    {
        var rows = await db.Balances.Where(x => x.AccountId == id && x.Available >= amount).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Available, x => x.Available - amount)
            .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (rows == 1) return WithdrawOutcome.Ok;

        return await db.Balances.AsNoTracking().AnyAsync(x => x.AccountId == id, ct)
            ? WithdrawOutcome.Insufficient
            : WithdrawOutcome.NotFound;
    }

    // forUpdate = pessimistic lock (SELECT ... FOR UPDATE); otherwise the isolation level alone protects us
    // (RepeatableRead / Serializable fail with 40001 and TxRetry repeats the whole transaction).
    private Task<WithdrawOutcome> TxAsync(long id, decimal amount, IsolationLevel level, bool forUpdate, CancellationToken ct) =>
        TxRetry.RunAsync(db, level, async () =>
        {
            var rows = forUpdate
                ? await db.Balances.FromSqlInterpolated(
                    $"SELECT * FROM accounts.balances WHERE account_id = {id} FOR UPDATE").ToListAsync(ct)
                : await db.Balances.Where(x => x.AccountId == id).ToListAsync(ct);

            var b = rows.SingleOrDefault();
            if (b is null) return WithdrawOutcome.NotFound;
            if (b.Available < amount) return WithdrawOutcome.Insufficient;

            b.Available -= amount;
            b.Version++;
            await db.SaveChangesAsync(ct);
            return WithdrawOutcome.Ok;
        }, ct);

    // No transaction and no lock: UPDATE ... WHERE version = @old; on 0 rows reload and try again.
    private async Task<WithdrawOutcome> OptimisticAsync(long id, decimal amount, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            db.ChangeTracker.Clear();
            var b = await db.Balances.FirstOrDefaultAsync(x => x.AccountId == id, ct);
            if (b is null) return WithdrawOutcome.NotFound;
            if (b.Available < amount) return WithdrawOutcome.Insufficient;

            b.Available -= amount;
            b.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
                return WithdrawOutcome.Ok;
            }
            catch (DbUpdateConcurrencyException)
            {
                AccountsMetrics.OptimisticConflicts.Add(1);
            }
        }
        return WithdrawOutcome.Conflict;
    }

    // ---------------------------------------------------------------- transfer (case C2)

    public async Task<TransferOutcome> TransferAsync(long fromId, long toId, decimal amount, TransferMode mode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        TransferOutcome? outcome = null;
        try
        {
            outcome = await TxRetry.RunAsync(db, IsolationLevel.ReadCommitted, async () =>
            {
                List<Balance> rows;
                if (mode == TransferMode.Ordered)
                {
                    // one statement, locks are taken in ascending account_id order
                    rows = await db.Balances.FromSqlInterpolated(
                        $"SELECT * FROM accounts.balances WHERE account_id IN ({fromId}, {toId}) ORDER BY account_id FOR UPDATE")
                        .ToListAsync(ct);
                }
                else
                {
                    // DELIBERATE: lock in request order, so A->B and B->A form a wait cycle (deadlock)
                    rows = [];
                    foreach (var lockId in new[] { fromId, toId })
                        rows.AddRange(await db.Balances.FromSqlInterpolated(
                            $"SELECT * FROM accounts.balances WHERE account_id = {lockId} FOR UPDATE").ToListAsync(ct));
                }

                var src = rows.FirstOrDefault(r => r.AccountId == fromId);
                var dst = rows.FirstOrDefault(r => r.AccountId == toId);
                if (src is null || dst is null) return TransferOutcome.NotFound;
                if (src.Available < amount) return TransferOutcome.Insufficient;

                src.Available -= amount;
                src.Version++;
                dst.Available += amount;
                dst.Version++;
                db.Outbox.Add(OutboxMessage.Create("TransferCompleted",
                    new { fromAccountId = fromId, toAccountId = toId, amount }));
                await db.SaveChangesAsync(ct);
                return TransferOutcome.Ok;
            }, ct, max: mode == TransferMode.Unordered ? 1 : 5);
        }
        finally
        {
            AccountsMetrics.Transfer.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("mode", mode.ToString()),
                new KeyValuePair<string, object?>("outcome", outcome?.ToString() ?? "Error"));
        }
        return outcome.Value;
    }

    // ---------------------------------------------------------------- reservations (used by Orders)

    public Task<ReserveResult> ReserveAsync(Guid reservationId, long accountId, decimal amount, CancellationToken ct = default) =>
        TxRetry.RunAsync(db, IsolationLevel.ReadCommitted, async () =>
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO accounts.reservations (id, account_id, amount, status, created_at)
                VALUES ({reservationId}, {accountId}, {amount}, 'Active', now())
                ON CONFLICT (id) DO NOTHING", ct);
            if (inserted == 0) return ReserveResult.Reserved; // repeated call: already reserved

            var rows = await db.Balances.Where(b => b.AccountId == accountId && b.Available >= amount).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Available, b => b.Available - amount)
                .SetProperty(b => b.Reserved, b => b.Reserved + amount)
                .SetProperty(b => b.Version, b => b.Version + 1), ct);
            if (rows == 1) return ReserveResult.Reserved;

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM accounts.reservations WHERE id = {reservationId}", ct);
            return await ExistsAsync(accountId, ct) ? ReserveResult.InsufficientFunds : ReserveResult.AccountNotFound;
        }, ct);

    public async Task ReleaseAsync(Guid reservationId, CancellationToken ct = default)
    {
        await TxRetry.RunAsync(db, IsolationLevel.ReadCommitted, async () =>
        {
            var reservation = (await db.Reservations.FromSqlInterpolated(
                    $"SELECT * FROM accounts.reservations WHERE id = {reservationId} AND status = 'Active' FOR UPDATE")
                .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
            if (reservation is null) return 0; // unknown or already released

            await db.Reservations.Where(r => r.Id == reservationId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ReservationStatus.Released), ct);
            var amount = reservation.Amount;
            await db.Balances.Where(b => b.AccountId == reservation.AccountId).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Available, b => b.Available + amount)
                .SetProperty(b => b.Reserved, b => b.Reserved - amount)
                .SetProperty(b => b.Version, b => b.Version + 1), ct);
            return 1;
        }, ct);
    }
}