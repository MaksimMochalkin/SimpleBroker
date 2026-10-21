namespace SimpleBroker.Trades.Contracts;

public sealed record TradeDto(
    long Id,
    int InstrumentId,
    long BuyOrderId,
    long SellOrderId,
    long BuyAccountId,
    long SellAccountId,
    decimal Price,
    decimal Quantity,
    DateTime ExecutedAt);

public sealed record TradesPage(int Page, int Size, IReadOnlyList<TradeDto> Items);
