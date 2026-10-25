namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using SimpleBroker.Accounts.Contracts;

using Xunit;
using Xunit.Abstractions;

[Collection("broker")]
public class WithdrawTests(BrokerFixture fx, ITestOutputHelper output)
{
    [Fact]
    public async Task Withdraw_reduces_balance_and_bumps_version()
    {
        var id = await fx.Client.NewAccountAsync(100m);

        var r = await fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw", new WithdrawRequest(30.5m));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var b = await fx.Client.BalanceAsync(id);
        Assert.Equal(69.5m, b.Available);
        Assert.Equal(2, b.Version); // deposit + withdraw
    }

    [Fact]
    public async Task Withdraw_more_than_available_is_422()
    {
        var id = await fx.Client.NewAccountAsync(10m);
        var r = await fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw", new WithdrawRequest(10.01m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(10m, (await fx.Client.BalanceAsync(id)).Available);
    }

    [Fact]
    public async Task Withdraw_from_missing_account_is_404()
    {
        var r = await fx.Client.PostAsJsonAsync("/accounts/987654321/withdraw", new WithdrawRequest(1m));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Non_positive_withdraw_is_400()
    {
        var id = await fx.Client.NewAccountAsync(10m);
        var r = await fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw", new WithdrawRequest(0m));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Unknown_mode_is_400()
    {
        var id = await fx.Client.NewAccountAsync(10m);
        var r = await fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw?mode=Bogus", new WithdrawRequest(1m));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // C1 in miniature: 200 parallel withdrawals of 1 from a balance of 100.
    [Theory]
    [InlineData("Atomic")]
    [InlineData("ForUpdate")]
    [InlineData("Optimistic")]
    [InlineData("RepeatableRead")]
    [InlineData("Serializable")]
    public async Task Safe_modes_keep_the_invariant_under_contention(string mode)
    {
        const decimal deposit = 100m;
        const int ops = 200;
        var id = await fx.Client.NewAccountAsync(deposit);

        var codes = await Api.RunParallelAsync(ops, 32, _ =>
            fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw?mode={mode}", new WithdrawRequest(1m)));

        output.WriteLine($"{mode}: {string.Join(", ", codes.OrderBy(c => c.Key).Select(c => $"{c.Key}={c.Value}"))}");
        // 409 = retries exhausted; a 5xx is a bug and we want to see why
        Assert.True(codes.Keys.All(c => c is 200 or 409 or 422),
            $"unexpected status codes: {string.Join(", ", codes)}\nserver errors:\n{string.Join("\n---\n", Api.ServerErrors)}");

        var ok = codes.GetValueOrDefault(200);
        var b = await fx.Client.BalanceAsync(id);
        Assert.Equal(deposit - ok, b.Available);      // no lost update
        Assert.InRange(ok, 0, (int)deposit);          // no oversell
        Assert.Equal(1 + ok, b.Version);              // every success bumped the version exactly once

        if (mode is "Atomic" or "ForUpdate")
            Assert.Equal((int)deposit, ok);           // these never give up: exactly 100 succeed
    }

    // Demonstration, not a regression test: run explicitly with --filter Category=Demo and read the output.
    [Fact]
    [Trait("Category", "Demo")]
    public async Task Naive_mode_shows_lost_updates()
    {
        const decimal deposit = 100m;
        var id = await fx.Client.NewAccountAsync(deposit);

        var codes = await Api.RunParallelAsync(400, 64, _ =>
            fx.Client.PostAsJsonAsync($"/accounts/{id}/withdraw?mode=Naive", new WithdrawRequest(1m)));

        var ok = codes.GetValueOrDefault(200);
        var actual = (await fx.Client.BalanceAsync(id)).Available;
        output.WriteLine($"Naive: ok={ok} (max {deposit}) expected={deposit - ok} actual={actual} lost={actual - (deposit - ok)}");
        Assert.True(ok > 0);
    }
}