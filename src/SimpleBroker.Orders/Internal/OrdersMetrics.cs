namespace SimpleBroker.Orders.Internal;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public static class OrdersMetrics
{
    public const string Name = "SimpleBroker.Orders";

    public static readonly ActivitySource Activity = new(Name);
    private static readonly Meter _meter = new(Name);

    public static readonly Counter<long> Created = _meter.CreateCounter<long>("simple_broker.orders.created");
}
