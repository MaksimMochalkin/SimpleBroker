namespace SimpleBroker.Accounts.Internal;

using System.Diagnostics.Metrics;

public static class AccountsMetrics
{
    public const string Name = "SimpleBroker.Accounts";

    private static readonly Meter _meter = new(Name);

    /// <summary>Tags: mode, outcome (Error = unexpected or retryable exception that escaped).</summary>
    public static readonly Histogram<double> Withdraw =
        _meter.CreateHistogram<double>("simple_broker.accounts.withdraw.duration_ms", unit: "ms");

    public static readonly Histogram<double> Transfer =
        _meter.CreateHistogram<double>("simple_broker.accounts.transfer.duration_ms", unit: "ms");

    public static readonly Counter<long> OptimisticConflicts =
        _meter.CreateCounter<long>("simple_broker.accounts.optimistic_conflicts");
}
