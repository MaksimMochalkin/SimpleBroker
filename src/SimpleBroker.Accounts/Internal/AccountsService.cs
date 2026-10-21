namespace SimpleBroker.Accounts.Internal;

using Microsoft.EntityFrameworkCore;

using SimpleBroker.Accounts.Contracts;

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
}
