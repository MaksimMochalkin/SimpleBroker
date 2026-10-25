namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Accounts.Internal;
using SimpleBroker.Orders.Contracts;
using SimpleBroker.Orders.Internal;

using Xunit;

[Collection("broker")]
public class ReservationTests(BrokerFixture fx)
{
    private const int InstrumentId = 7002;

    [Fact]
    public async Task Buy_limit_reserves_price_times_quantity()
    {
        await fx.SeedInstrumentAsync(InstrumentId);
        var account = await fx.Client.NewAccountAsync(1000m);

        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Limit", 10m, 5m));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await fx.Client.BalanceAsync(account);
        Assert.Equal(950m, b.Available);
        Assert.Equal(50m, b.Reserved);
    }

    [Fact]
    public async Task Insufficient_funds_is_422_and_leaves_no_order_and_no_reservation()
    {
        await fx.SeedInstrumentAsync(InstrumentId);
        var account = await fx.Client.NewAccountAsync(40m);

        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Limit", 10m, 5m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        var b = await fx.Client.BalanceAsync(account);
        Assert.Equal(40m, b.Available);
        Assert.Equal(0m, b.Reserved);

        await fx.WithDb<OrdersDbContext>(async db =>
            Assert.Equal(0, await db.Orders.CountAsync(o => o.AccountId == account)));
        await fx.WithDb<AccountsDbContext>(async db =>
            Assert.Equal(0, await db.Reservations.CountAsync(x => x.AccountId == account)));
    }

    [Fact]
    public async Task Sell_does_not_reserve()
    {
        await fx.SeedInstrumentAsync(InstrumentId);
        var account = await fx.Client.NewAccountAsync(100m);

        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Sell", "Limit", 10m, 5m));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await fx.Client.BalanceAsync(account);
        Assert.Equal(100m, b.Available);
        Assert.Equal(0m, b.Reserved);
    }

    [Fact]
    public async Task Reserve_is_idempotent_and_release_restores_the_balance()
    {
        var account = await fx.Client.NewAccountAsync(1000m);
        using var scope = fx.Factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IAccountsApi>();
        var rid = Guid.NewGuid();

        Assert.Equal(ReserveResult.Reserved, await api.ReserveAsync(rid, account, 100m));
        Assert.Equal(ReserveResult.Reserved, await api.ReserveAsync(rid, account, 100m)); // repeat: no second reservation
        var reserved = await fx.Client.BalanceAsync(account);
        Assert.Equal(900m, reserved.Available);
        Assert.Equal(100m, reserved.Reserved);

        await api.ReleaseAsync(rid);
        await api.ReleaseAsync(rid); // repeat: no double release
        var released = await fx.Client.BalanceAsync(account);
        Assert.Equal(1000m, released.Available);
        Assert.Equal(0m, released.Reserved);
    }

    [Fact]
    public async Task Reserve_reports_missing_account_and_insufficient_funds()
    {
        var account = await fx.Client.NewAccountAsync(10m);
        using var scope = fx.Factory.Services.CreateScope();
        var api = scope.ServiceProvider.GetRequiredService<IAccountsApi>();

        Assert.Equal(ReserveResult.AccountNotFound, await api.ReserveAsync(Guid.NewGuid(), 987654321, 1m));
        Assert.Equal(ReserveResult.InsufficientFunds, await api.ReserveAsync(Guid.NewGuid(), account, 10.01m));
        Assert.Equal(0m, (await fx.Client.BalanceAsync(account)).Reserved);
    }
}
