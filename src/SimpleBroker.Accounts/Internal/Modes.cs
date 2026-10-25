namespace SimpleBroker.Accounts.Internal;

using SimpleBroker.Accounts.Contracts;

/// <summary>Concurrency strategies for withdraw (stage 2, case C1). Atomic is the default; Naive is a deliberate bug.</summary>
public enum WithdrawMode { Naive, Atomic, ForUpdate, Optimistic, RepeatableRead, Serializable }

public enum WithdrawOutcome { Ok, NotFound, Insufficient, Conflict }

public sealed record WithdrawResult(WithdrawOutcome Outcome, BalanceResponse? Balance);

/// <summary>Lock ordering strategies for transfer (case C2). Ordered is the default.</summary>
public enum TransferMode { Unordered, UnorderedRetry, Ordered }

public enum TransferOutcome { Ok, NotFound, Insufficient }
