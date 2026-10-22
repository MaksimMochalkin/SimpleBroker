namespace SimpleBroker.Trades.Internal;

using Microsoft.EntityFrameworkCore;

using SimpleBroker.Trades.Contracts;

public sealed class TradesService(TradesDbContext db)
{
    /// <summary>
    /// Intentionally naive for the baseline: OR over two columns, OFFSET pagination, no secondary indexes.
    /// Fixed in stage 3 (indexes, keyset pagination).
    /// </summary>
    public async Task<TradesPage> GetForAccountAsync(long accountId, int page, int size, CancellationToken ct)
    {
        var items = await db.Trades.AsNoTracking()
            .Where(t => t.BuyAccountId == accountId || t.SellAccountId == accountId)
            .OrderByDescending(t => t.ExecutedAt).ThenByDescending(t => t.Id)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(t => new TradeDto(t.Id, t.InstrumentId, t.BuyOrderId, t.SellOrderId,
                t.BuyAccountId, t.SellAccountId, t.Price, t.Quantity, t.ExecutedAt))
            .ToListAsync(ct);

        return new TradesPage(page, size, items);
    }
}
