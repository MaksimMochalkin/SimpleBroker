namespace SimpleBroker.Orders.Internal;

using Microsoft.EntityFrameworkCore;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Orders.Contracts;

public enum CreateOrderOutcome { Created, Invalid, AccountNotFound, InstrumentNotFound }

public sealed record CreateOrderResult(CreateOrderOutcome Outcome, long? Id = null, string? Error = null);

public sealed class OrdersService(OrdersDbContext db, IAccountsApi accounts)
{
    public async Task<CreateOrderResult> CreateAsync(CreateOrderRequest r, CancellationToken ct)
    {
        using var activity = OrdersMetrics.Activity.StartActivity("orders.create");

        if (!Enum.TryParse<OrderSide>(r.Side, ignoreCase: true, out var side) || !Enum.IsDefined(side))
            return Invalid("side must be Buy or Sell");
        if (!Enum.TryParse<OrderType>(r.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type))
            return Invalid("type must be Limit or Market");
        if (r.Quantity <= 0)
            return Invalid("quantity must be positive");
        if (type == OrderType.Limit && r.Price is not > 0)
            return Invalid("price must be positive for Limit orders");

        if (!await accounts.ExistsAsync(r.AccountId, ct))
            return new CreateOrderResult(CreateOrderOutcome.AccountNotFound, Error: "account not found");

        if (!await db.Instruments.AsNoTracking().AnyAsync(i => i.Id == r.InstrumentId, ct))
            return new CreateOrderResult(CreateOrderOutcome.InstrumentNotFound, Error: "instrument not found");

        var order = new Order
        {
            AccountId = r.AccountId,
            InstrumentId = r.InstrumentId,
            Side = side,
            Type = type,
            Price = type == OrderType.Limit ? r.Price : null,
            Quantity = r.Quantity,
            FilledQuantity = 0,
            Status = OrderStatus.New,
            CreatedAt = DateTime.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        OrdersMetrics.Created.Add(1, new KeyValuePair<string, object?>("side", side.ToString()));
        return new CreateOrderResult(CreateOrderOutcome.Created, order.Id);
    }

    private static CreateOrderResult Invalid(string error) => new(CreateOrderOutcome.Invalid, Error: error);
}
