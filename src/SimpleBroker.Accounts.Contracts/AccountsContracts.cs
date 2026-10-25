namespace SimpleBroker.Accounts.Contracts;

/// <summary>The only way other modules talk to Accounts.</summary>
public interface IAccountsApi
{
    Task<bool> ExistsAsync(long accountId, CancellationToken ct = default);

    /// <summary>Moves <paramref name="amount"/> from available to reserved. Idempotent by <paramref name="reservationId"/>.</summary>
    Task<ReserveResult> ReserveAsync(Guid reservationId, long accountId, decimal amount, CancellationToken ct = default);

    /// <summary>Returns a reservation to available. Idempotent: unknown or already released ids are ignored.</summary>
    Task ReleaseAsync(Guid reservationId, CancellationToken ct = default);
}

public enum ReserveResult { Reserved, InsufficientFunds, AccountNotFound }

public sealed record CreateAccountRequest(string Owner, string Currency);

public sealed record AccountCreatedResponse(long Id);

public sealed record DepositRequest(decimal Amount);

public sealed record WithdrawRequest(decimal Amount);

public sealed record TransferRequest(long FromAccountId, long ToAccountId, decimal Amount);

public sealed record TransferResponse(long FromAccountId, long ToAccountId, decimal Amount);

public sealed record BalanceResponse(long AccountId, decimal Available, decimal Reserved, long Version);