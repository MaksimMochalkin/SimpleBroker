namespace SimpleBroker.Platform;

using System.Diagnostics.Metrics;

public static class PlatformMetrics
{
    public const string Name = "SimpleBroker.Platform";

    private static readonly Meter _meter = new(Name);

    /// <summary>Tag <c>reason</c>: deadlock | serialization.</summary>
    public static readonly Counter<long> Retries = _meter.CreateCounter<long>("simple_broker.db.retries");

    /// <summary>Tag <c>schema</c>: module that owns the outbox table.</summary>
    public static readonly Counter<long> OutboxPublished = _meter.CreateCounter<long>("simple_broker.outbox.published");

    public static readonly Histogram<double> OutboxLag = _meter.CreateHistogram<double>("simple_broker.outbox.lag_seconds", unit: "s");
}
