namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Orders.Contracts;
using SimpleBroker.Orders.Internal;

using Xunit;

[Collection("broker")]
public class OrdersTests(BrokerFixture fx)
{
    private const int InstrumentId = 7001;

    // Buy orders reserve funds since stage 2, so accounts get a deposit by default.
    private Task<long> NewAccount(decimal deposit = 100_000m) => fx.Client.NewAccountAsync(deposit);

    private Task SeedInstrument() => fx.WithDb<OrdersDbContext>(async db =>
    {
        if (await db.Instruments.FindAsync(InstrumentId) is null)
        {
            db.Instruments.Add(new Instrument { Id = InstrumentId, Symbol = "TST", TickSize = 0.01m });
            await db.SaveChangesAsync();
        }
    });

    [Fact]
    public async Task Limit_order_is_created_with_status_New()
    {
        await SeedInstrument();
        var account = await NewAccount();

        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Limit", 101.5m, 10m));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<OrderCreatedResponse>();
        Assert.True(body!.Id > 0);
        Assert.Equal("New", body.Status);
    }

    [Fact]
    public async Task Unknown_account_is_404()
    {
        await SeedInstrument();
        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(987654321, InstrumentId, "Sell", "Limit", 10m, 1m));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_instrument_is_404()
    {
        var account = await NewAccount();
        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, 424242, "Buy", "Limit", 10m, 1m));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("Hold", "Limit", 10d, 1d)]   // bad side
    [InlineData("Buy", "Stop", 10d, 1d)]     // bad type
    [InlineData("Buy", "Limit", 10d, 0d)]    // zero quantity
    [InlineData("Buy", "Limit", 0d, 1d)]     // limit without price
    [InlineData("5", "Limit", 10d, 1d)]      // numeric enum string must not slip through
    public async Task Invalid_orders_are_400(string side, string type, double price, double qty)
    {
        await SeedInstrument();
        var account = await NewAccount();
        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, side, type, (decimal)price, (decimal)qty));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Market_sell_needs_no_price()
    {
        await SeedInstrument();
        var account = await NewAccount();
        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Sell", "Market", null, 3m));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task Market_buy_requires_a_price_cap()
    {
        await SeedInstrument();
        var account = await NewAccount();

        var withoutCap = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Market", null, 3m));
        Assert.Equal(HttpStatusCode.BadRequest, withoutCap.StatusCode);

        var withCap = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Market", 20m, 3m));
        Assert.Equal(HttpStatusCode.Created, withCap.StatusCode);
    }
}