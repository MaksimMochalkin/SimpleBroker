namespace SimpleBroker.IntegrationTests;

using System.Net;
using System.Net.Http.Json;

using SimpleBroker.Accounts.Contracts;

using Xunit;

[Collection("broker")]
public class AccountsTests(BrokerFixture fx)
{
    [Fact]
    public async Task Create_deposit_and_read_balance()
    {
        var created = await fx.Client.PostAsJsonAsync("/accounts", new CreateAccountRequest("alice", "usd"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<AccountCreatedResponse>())!.Id;

        var dep1 = await fx.Client.PostAsJsonAsync($"/accounts/{id}/deposit", new DepositRequest(100.50m));
        Assert.Equal(HttpStatusCode.OK, dep1.StatusCode);
        await fx.Client.PostAsJsonAsync($"/accounts/{id}/deposit", new DepositRequest(0.25m));

        var balance = await fx.Client.GetFromJsonAsync<BalanceResponse>($"/accounts/{id}/balance");
        Assert.Equal(100.75m, balance!.Available);
        Assert.Equal(0m, balance.Reserved);
        Assert.Equal(2, balance.Version);
    }

    [Fact]
    public async Task Deposit_to_missing_account_is_404()
    {
        var r = await fx.Client.PostAsJsonAsync("/accounts/999999/deposit", new DepositRequest(10m));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("", "USD")]
    [InlineData("bob", "US")]
    public async Task Create_with_invalid_input_is_400(string owner, string currency)
    {
        var r = await fx.Client.PostAsJsonAsync("/accounts", new CreateAccountRequest(owner, currency));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Non_positive_deposit_is_400()
    {
        var created = await fx.Client.PostAsJsonAsync("/accounts", new CreateAccountRequest("carol", "EUR"));
        var id = (await created.Content.ReadFromJsonAsync<AccountCreatedResponse>())!.Id;

        var r = await fx.Client.PostAsJsonAsync($"/accounts/{id}/deposit", new DepositRequest(0m));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
