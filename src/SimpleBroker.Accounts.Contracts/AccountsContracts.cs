namespace SimpleBroker.Accounts.Contracts;

/// <summary>The only way other modules talk to Accounts.</summary>
public interface IAccountsApi
{
    Task<bool> ExistsAsync(long accountId, CancellationToken ct = default);
}

public sealed record CreateAccountRequest(string Owner, string Currency);

public sealed record AccountCreatedResponse(long Id);

public sealed record DepositRequest(decimal Amount);

public sealed record BalanceResponse(long AccountId, decimal Available, decimal Reserved, long Version);
