using System.Net;
using System.Net.Http.Json;
using SimpleBroker.Trades.Contracts;
using SimpleBroker.Trades.Internal;
using Xunit;

namespace SimpleBroker.IntegrationTests;

[Collection("broker")]
public class TradesTests(BrokerFixture fx)
{
    private const long Account = 5_000_001;

    private async Task Seed()
    {
        await fx.WithDb<TradesDbContext>(async db =>
        {
            if (db.Trades.Any(t => t.BuyAccountId == Account || t.SellAccountId == Account)) return;

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

        var p1 = await fx.Client.GetFromJsonAsync<TradesPage>($"/accounts/{Account}/trades?page=1&size=10");
        var p3 = await fx.Client.GetFromJsonAsync<TradesPage>($"/accounts/{Account}/trades?page=3&size=10");

        Assert.Equal(10, p1!.Items.Count);
        Assert.Equal(5, p3!.Items.Count);
        Assert.Equal(9_025, p1.Items[0].Id);
        Assert.All(p1.Items, t => Assert.True(t.BuyAccountId == Account || t.SellAccountId == Account));
        Assert.True(p1.Items.Zip(p1.Items.Skip(1)).All(p => p.First.ExecutedAt >= p.Second.ExecutedAt));
    }

    [Theory]
    [InlineData("page=0&size=10")]
    [InlineData("page=1&size=0")]
    [InlineData("page=1&size=1000")]
    public async Task Invalid_paging_is_400(string query)
    {
        var r = await fx.Client.GetAsync($"/accounts/{Account}/trades?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
