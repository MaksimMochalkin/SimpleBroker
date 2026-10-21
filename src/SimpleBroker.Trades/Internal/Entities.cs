namespace SimpleBroker.Trades.Internal;

public sealed class Trade
{
    public long Id { get; set; }
    public int InstrumentId { get; set; }
    public long BuyOrderId { get; set; }
    public long SellOrderId { get; set; }
    public long BuyAccountId { get; set; }
    public long SellAccountId { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime ExecutedAt { get; set; }
}
