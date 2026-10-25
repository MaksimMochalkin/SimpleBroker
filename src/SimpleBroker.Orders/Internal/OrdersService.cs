namespace SimpleBroker.Orders.Internal;

using Microsoft.EntityFrameworkCore;
using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Orders.Contracts;
using SimpleBroker.Platform.Outbox;

public enum CreateOrderOutcome { Created, Invalid, AccountNotFound, InstrumentNotFound, InsufficientFunds }

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
        // Limit needs a price; Market Buy needs one too: it is the price cap that the reservation is based on.
        if ((type == OrderType.Limit || side == OrderSide.Buy) && r.Price is not > 0)
            return Invalid("price must be positive (for a Market Buy it is the maximum price)");

        // Check the instrument before reserving, so a bad request does not touch balances.
        if (!await db.Instruments.AsNoTracking().AnyAsync(i => i.Id == r.InstrumentId, ct))
            return new CreateOrderResult(CreateOrderOutcome.InstrumentNotFound, Error: "instrument not found");

        // Accounts and Orders have separate DbContexts (ADR-0001): reserve first, then insert the order,
        // and release the reservation if the insert fails. A crash between the two leaves an orphan reservation (stage 10).
        Guid? reservationId = null;
        if (side == OrderSide.Buy)
        {
            reservationId = Guid.NewGuid();
            var reserve = await accounts.ReserveAsync(reservationId.Value, r.AccountId, r.Price!.Value * r.Quantity, ct);
            if (reserve == ReserveResult.AccountNotFound)
                return new CreateOrderResult(CreateOrderOutcome.AccountNotFound, Error: "account not found");
            if (reserve == ReserveResult.InsufficientFunds)
                return new CreateOrderResult(CreateOrderOutcome.InsufficientFunds, Error: "insufficient funds");
        }
        else if (!await accounts.ExistsAsync(r.AccountId, ct))
        {
            return new CreateOrderResult(CreateOrderOutcome.AccountNotFound, Error: "account not found");
        }

        var order = new Order
        {
            AccountId = r.AccountId,
            InstrumentId = r.InstrumentId,
            Side = side,
            Type = type,
            Price = type == OrderType.Limit || side == OrderSide.Buy ? r.Price : null,
            Quantity = r.Quantity,
            FilledQuantity = 0,
            Status = OrderStatus.New,
            CreatedAt = DateTime.UtcNow,
            ReservationId = reservationId,
        };

        try
        {
            // order and outbox message commit together or not at all
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            db.Outbox.Add(OutboxMessage.Create("OrderCreated", new
            {
                orderId = order.Id,
                accountId = order.AccountId,
                instrumentId = order.InstrumentId,
                side = side.ToString(),
                type = type.ToString(),
                price = order.Price,
                quantity = order.Quantity,
                reservationId,
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            if (reservationId is not null)
                await accounts.ReleaseAsync(reservationId.Value, CancellationToken.None);
            throw;
        }

        OrdersMetrics.Created.Add(1, new KeyValuePair<string, object?>("side", side.ToString()));
        return new CreateOrderResult(CreateOrderOutcome.Created, order.Id);
    }

    private static CreateOrderResult Invalid(string error) => new(CreateOrderOutcome.Invalid, Error: error);
}