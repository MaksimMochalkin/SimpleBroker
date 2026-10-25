namespace SimpleBroker.IntegrationTests;

using System.Collections.Concurrent;
using System.Net.Http.Json;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Orders.Internal;

/// <summary>Small helpers shared by the stage-2 tests.</summary>
internal static class Api
{
    /// <summary>First few 5xx response bodies of the last <see cref="RunParallelAsync"/> call (test diagnostics).</summary>
    public static readonly ConcurrentQueue<string> ServerErrors = new();

    public static async Task<long> NewAccountAsync(this HttpClient c, decimal deposit = 0m)
    {
        var r = await c.PostAsJsonAsync("/accounts", new CreateAccountRequest("tester", "USD"));
        var id = (await r.Content.ReadFromJsonAsync<AccountCreatedResponse>())!.Id;
        if (deposit > 0)
            (await c.PostAsJsonAsync($"/accounts/{id}/deposit", new DepositRequest(deposit))).EnsureSuccessStatusCode();
        return id;
    }

    public static async Task<BalanceResponse> BalanceAsync(this HttpClient c, long id) =>
        (await c.GetFromJsonAsync<BalanceResponse>($"/accounts/{id}/balance"))!;

    /// <summary>Runs <paramref name="call"/> <paramref name="ops"/> times with the given parallelism; returns status code counts.</summary>
    public static async Task<IReadOnlyDictionary<int, int>> RunParallelAsync(
        int ops, int parallel, Func<int, Task<HttpResponseMessage>> call)
    {
        var codes = new ConcurrentDictionary<int, int>();
        ServerErrors.Clear();
        await Parallel.ForEachAsync(Enumerable.Range(0, ops), new ParallelOptions { MaxDegreeOfParallelism = parallel },
            async (i, ct) =>
            {
                using var r = await call(i);
                if ((int)r.StatusCode >= 500 && ServerErrors.Count < 3)
                {
                    var text = await r.Content.ReadAsStringAsync();
                    ServerErrors.Enqueue(text.Length > 1500 ? text[..1500] : text);
                }
                codes.AddOrUpdate((int)r.StatusCode, 1, (key, n) => n + 1);
            });
        return codes;
    }

    public static Task SeedInstrumentAsync(this BrokerFixture fx, int id) => fx.WithDb<OrdersDbContext>(async db =>
    {
        if (await db.Instruments.FindAsync(id) is null)
        {
            db.Instruments.Add(new Instrument { Id = id, Symbol = $"T{id}", TickSize = 0.01m });
            await db.SaveChangesAsync();
        }
    });

    /// <summary>Polls until <paramref name="probe"/> returns something or the timeout expires.</summary>
    public static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe, int seconds = 10) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var value = await probe();
            if (value is not null) return value;
            await Task.Delay(100);
        }
        throw new TimeoutException($"condition not met within {seconds}s");
    }
}