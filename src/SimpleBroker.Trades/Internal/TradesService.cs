namespace SimpleBroker.Trades.Internal;

using Microsoft.EntityFrameworkCore;
using SimpleBroker.Trades.Contracts;

public sealed class TradesService(TradesDbContext db)
{
    internal async Task<TradesPage> GetForAccountAsync(
        long accountId,
        int size,
        TradeCursorValue? cursor,
        CancellationToken ct)
    {
        var buys = db.Trades.AsNoTracking()
            .Where(t => t.BuyAccountId == accountId);
        var sells = db.Trades.AsNoTracking()
            .Where(t => t.SellAccountId == accountId && t.BuyAccountId != accountId);

        if (cursor is { } before)
        {
            buys = buys.Where(t => t.ExecutedAt < before.ExecutedAt ||
                                   (t.ExecutedAt == before.ExecutedAt && t.Id < before.Id));
            sells = sells.Where(t => t.ExecutedAt < before.ExecutedAt ||
                                     (t.ExecutedAt == before.ExecutedAt && t.Id < before.Id));
        }

        var rows = await buys.Concat(sells)
            .OrderByDescending(t => t.ExecutedAt)
            .ThenByDescending(t => t.Id)
            .Take(size + 1)
            .Select(t => new TradeDto(t.Id, t.InstrumentId, t.BuyOrderId, t.SellOrderId,
                t.BuyAccountId, t.SellAccountId, t.Price, t.Quantity, t.ExecutedAt))
            .ToListAsync(ct);

        var hasMore = rows.Count > size;
        if (hasMore)
            rows.RemoveAt(size);

        var nextCursor = hasMore && rows.Count > 0
            ? TradeCursorCodec.Encode(rows[^1].ExecutedAt, rows[^1].Id)
            : null;

        return new TradesPage(size, rows, nextCursor);
    }

}
