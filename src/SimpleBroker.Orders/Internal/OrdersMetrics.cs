using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SimpleBroker.Orders.Internal;

public static class OrdersMetrics
{
    public const string Name = "SimpleBroker.Orders";

    public static readonly ActivitySource Activity = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Created = Meter.CreateCounter<long>("simple_broker.orders.created");
}
