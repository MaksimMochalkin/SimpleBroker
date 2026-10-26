namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using SimpleBroker.Trades.Contracts;
using SimpleBroker.Trades.Internal;

using Xunit;

[Collection("broker")]
public class TradesTests(BrokerFixture fx)
{
    private const long Account = 5_000_001;
    private const long CursorAccount = 5_000_002;

    private async Task Seed()
    {
        await fx.WithDb<TradesDbContext>(async db =>
        {
            if (db.Trades.Any(t => t.Id >= 9_001 && t.Id <= 9_025)) return;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            for (var i = 1; i <= 25; i++)
            {
                db.Trades.Add(new Trade
                {
                    Id = 9_000 + i,
                    InstrumentId = 1,
                    BuyOrderId = i,
                    SellOrderId = i + 1000,
                    BuyAccountId = i % 2 == 0 ? Account : 111,
                    SellAccountId = i % 2 == 0 ? 222 : Account,
                    Price = 100m,
                    Quantity = 1m,
                    ExecutedAt = t0.AddMinutes(i),
                });
            }
            // noise: a trade of someone else
            db.Trades.Add(new Trade { Id = 9_999, InstrumentId = 1, BuyOrderId = 1, SellOrderId = 2,
                BuyAccountId = 1, SellAccountId = 2, Price = 1m, Quantity = 1m, ExecutedAt = t0 });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Returns_trades_of_account_newest_first_with_paging()
    {
        await Seed();

        var p1 = await fx.Client.GetFromJsonAsync<TradesPage>($"/accounts/{Account}/trades?size=10");
        var p2 = await fx.Client.GetFromJsonAsync<TradesPage>(
            $"/accounts/{Account}/trades?size=10&cursor={Uri.EscapeDataString(p1!.NextCursor!)}");

        Assert.Equal(10, p1.Items.Count);
        Assert.Equal(10, p2!.Items.Count);
        Assert.Equal(9_025, p1.Items[0].Id);
        Assert.All(p1.Items, t => Assert.True(t.BuyAccountId == Account || t.SellAccountId == Account));
        Assert.True(p1.Items.Zip(p1.Items.Skip(1)).All(p => p.First.ExecutedAt >= p.Second.ExecutedAt));
        Assert.Equal(9_015, p2.Items[0].Id);
        Assert.Empty(p1.Items.Intersect(p2.Items));
    }

    [Fact]
    public async Task Cursor_uses_id_as_tie_breaker_and_does_not_duplicate_self_trades()
    {
        await fx.WithDb<TradesDbContext>(async db =>
        {
            db.Trades.AddRange(
                new Trade { Id = 20_001, InstrumentId = 1, BuyOrderId = 1, SellOrderId = 2,
                    BuyAccountId = CursorAccount, SellAccountId = CursorAccount, Price = 1m, Quantity = 1m,
                    ExecutedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
                new Trade { Id = 20_002, InstrumentId = 1, BuyOrderId = 3, SellOrderId = 4,
                    BuyAccountId = CursorAccount, SellAccountId = 333, Price = 1m, Quantity = 1m,
                    ExecutedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        });

        var page = await fx.Client.GetFromJsonAsync<TradesPage>(
            $"/accounts/{CursorAccount}/trades?size=1");
        var nextPage = await fx.Client.GetFromJsonAsync<TradesPage>(
            $"/accounts/{CursorAccount}/trades?size=1&cursor={Uri.EscapeDataString(page!.NextCursor!)}");

        Assert.Equal(20_002, Assert.Single(page.Items).Id);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(20_001, Assert.Single(nextPage!.Items).Id);
        Assert.Null(nextPage.NextCursor);
    }

    [Theory]
    [InlineData("page=0&size=10")]
    [InlineData("page=2&size=10")]
    [InlineData("size=0")]
    [InlineData("size=1000")]
    [InlineData("cursor=not-a-valid-cursor")]
    public async Task Invalid_paging_is_400(string query)
    {
        var r = await fx.Client.GetAsync($"/accounts/{Account}/trades?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}