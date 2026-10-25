namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using SimpleBroker.Accounts.Contracts;

using Xunit;
using Xunit.Abstractions;

[Collection("broker")]
public class TransferTests(BrokerFixture fx, ITestOutputHelper output)
{
    [Fact]
    public async Task Transfer_moves_money()
    {
        var a = await fx.Client.NewAccountAsync(100m);
        var b = await fx.Client.NewAccountAsync(5m);

        var r = await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, b, 40m));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(60m, (await fx.Client.BalanceAsync(a)).Available);
        Assert.Equal(45m, (await fx.Client.BalanceAsync(b)).Available);
    }

    [Fact]
    public async Task Transfer_validation_and_errors()
    {
        var a = await fx.Client.NewAccountAsync(10m);
        var b = await fx.Client.NewAccountAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, a, 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, b, 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, b, 11m))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, 987654321, 1m))).StatusCode);
        Assert.Equal(10m, (await fx.Client.BalanceAsync(a)).Available);
    }

    // C2 in miniature: opposing transfers between two accounts. Ordered locking must never deadlock.
    [Fact]
    public async Task Ordered_opposing_transfers_do_not_deadlock()
    {
        const decimal deposit = 1000m;
        var a = await fx.Client.NewAccountAsync(deposit);
        var b = await fx.Client.NewAccountAsync(deposit);

        var codes = await Api.RunParallelAsync(200, 32, i => i % 2 == 0
            ? fx.Client.PostAsJsonAsync("/transfers?mode=Ordered", new TransferRequest(a, b, 1m))
            : fx.Client.PostAsJsonAsync("/transfers?mode=Ordered", new TransferRequest(b, a, 1m)));

        Assert.Equal([200], codes.Keys.ToArray());
        var total = (await fx.Client.BalanceAsync(a)).Available + (await fx.Client.BalanceAsync(b)).Available;
        Assert.Equal(2 * deposit, total);
    }

    // Demonstration: Unordered gives 409 "deadlock" (Postgres waits deadlock_timeout, 1 s, before it picks a victim);
    // UnorderedRetry hides them behind retries. Money is never created or lost in either case.
    [Theory]
    [Trait("Category", "Demo")]
    [InlineData("Unordered")]
    [InlineData("UnorderedRetry")]
    public async Task Unordered_opposing_transfers_demo(string mode)
    {
        const decimal deposit = 1000m;
        var a = await fx.Client.NewAccountAsync(deposit);
        var b = await fx.Client.NewAccountAsync(deposit);

        var codes = await Api.RunParallelAsync(100, 16, i => i % 2 == 0
            ? fx.Client.PostAsJsonAsync($"/transfers?mode={mode}", new TransferRequest(a, b, 1m))
            : fx.Client.PostAsJsonAsync($"/transfers?mode={mode}", new TransferRequest(b, a, 1m)));

        output.WriteLine($"{mode}: {string.Join(", ", codes.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}");
        Assert.All(codes.Keys, c => Assert.Contains(c, new[] { 200, 409 }));
        var total = (await fx.Client.BalanceAsync(a)).Available + (await fx.Client.BalanceAsync(b)).Available;
        Assert.Equal(2 * deposit, total);
    }
}
