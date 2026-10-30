namespace SimpleBroker.Trades.Internal;

using Dapper;
using Npgsql;
using SimpleBroker.Trades.Contracts;

internal sealed class DapperTradesService(NpgsqlDataSource dataSource)
{
    public async Task<TradesPage> GetForAccountAsync(
        long accountId,
        int size,
        TradeCursorValue? cursor,
        CancellationToken ct)
    {
        var cursorPredicate = cursor is null
            ? string.Empty
            : """
              AND (t.executed_at < @ExecutedAt
                   OR (t.executed_at = @ExecutedAt AND t.id < @CursorId))
              """;

        var sql = $"""
            SELECT u."Id", u."InstrumentId", u."BuyOrderId", u."SellOrderId",
                   u."BuyAccountId", u."SellAccountId", u."Price", u."Quantity", u."ExecutedAt"
            FROM (
                (SELECT t.id AS "Id", t.instrument_id AS "InstrumentId",
                        t.buy_order_id AS "BuyOrderId", t.sell_order_id AS "SellOrderId",
                        t.buy_account_id AS "BuyAccountId", t.sell_account_id AS "SellAccountId",
                        t.price AS "Price", t.quantity AS "Quantity", t.executed_at AS "ExecutedAt"
                 FROM trades.trades AS t
                 WHERE t.buy_account_id = @AccountId
                 {cursorPredicate})
                UNION ALL
                (SELECT t.id AS "Id", t.instrument_id AS "InstrumentId",
                        t.buy_order_id AS "BuyOrderId", t.sell_order_id AS "SellOrderId",
                        t.buy_account_id AS "BuyAccountId", t.sell_account_id AS "SellAccountId",
                        t.price AS "Price", t.quantity AS "Quantity", t.executed_at AS "ExecutedAt"
                 FROM trades.trades AS t
                 WHERE t.sell_account_id = @AccountId
                   AND t.buy_account_id <> @AccountId
                 {cursorPredicate})
            ) AS u
            ORDER BY u."ExecutedAt" DESC, u."Id" DESC
            LIMIT @Limit
            """;

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        var command = new CommandDefinition(
            sql,
            new
            {
                AccountId = accountId,
                ExecutedAt = cursor?.ExecutedAt,
                CursorId = cursor?.Id,
                Limit = size + 1,
            },
            cancellationToken: ct);
        var rows = (await connection.QueryAsync<TradeDto>(command)).AsList();

        var hasMore = rows.Count > size;
        if (hasMore)
            rows.RemoveAt(size);

        var nextCursor = hasMore && rows.Count > 0
            ? TradeCursorCodec.Encode(rows[^1].ExecutedAt, rows[^1].Id)
            : null;

        return new TradesPage(size, rows, nextCursor);
    }
}
