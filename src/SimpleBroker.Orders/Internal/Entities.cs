namespace SimpleBroker.Orders.Internal;

public enum OrderSide { Buy, Sell }

public enum OrderType { Limit, Market }

public enum OrderStatus { New, PartiallyFilled, Filled, Cancelled }

public sealed class Instrument
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public decimal TickSize { get; set; }
}

public sealed class Order
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public int InstrumentId { get; set; }
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public decimal? Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
