namespace SimpleBroker.Orders.Contracts;

/// <param name="Side">"Buy" or "Sell".</param>
/// <param name="Type">"Limit" or "Market".</param>
public sealed record CreateOrderRequest(
    long AccountId,
    int InstrumentId,
    string Side,
    string Type,
    decimal? Price,
    decimal Quantity);

public sealed record OrderCreatedResponse(long Id, string Status);
