namespace SimpleBroker.IntegrationTests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using SimpleBroker.Accounts.Contracts;
using SimpleBroker.Accounts.Internal;
using SimpleBroker.Orders.Contracts;
using SimpleBroker.Orders.Internal;
using SimpleBroker.Platform.Outbox;

using Xunit;

[Collection("broker")]
public class OutboxTests(BrokerFixture fx)
{
    private const int InstrumentId = 7003;

    [Fact]
    public async Task Order_creation_publishes_OrderCreated()
    {
        await fx.SeedInstrumentAsync(InstrumentId);
        var account = await fx.Client.NewAccountAsync(1000m);
        var r = await fx.Client.PostAsJsonAsync("/orders",
            new CreateOrderRequest(account, InstrumentId, "Buy", "Limit", 10m, 1m));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"POST /orders -> {(int)r.StatusCode}: {body}");
        var orderId = JsonSerializer.Deserialize<OrderCreatedResponse>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Id;

        OutboxMessage message;
        try
        {
            message = await Api.EventuallyAsync(async () =>
            {
                OutboxMessage? found = null;
                await fx.WithDb<OrdersDbContext>(async db =>
                {
                    var rows = await db.Outbox.AsNoTracking()
                        .Where(m => m.Type == "OrderCreated" && m.PublishedAt != null).ToListAsync();
                    found = rows.FirstOrDefault(m =>
                        JsonDocument.Parse(m.Payload).RootElement.GetProperty("orderId").GetInt64() == orderId);
                });
                return found;
            });
        }
        catch (TimeoutException)
        {
            var dump = "";
            await fx.WithDb<OrdersDbContext>(async db =>
                dump = string.Join("\n", (await db.Outbox.AsNoTracking().OrderByDescending(m => m.Id).Take(5).ToListAsync())
                    .Select(m => $"{m.Id} {m.Type} published={m.PublishedAt != null} {m.Payload}")));
            throw new Xunit.Sdk.XunitException($"orderId={orderId}; last orders.outbox rows:\n{dump}\nhost warnings/errors:\n{string.Join("\n", fx.Logs.Lines)}");
        }

        Assert.NotNull(message.PublishedAt);
    }

    [Fact]
    public async Task Transfer_publishes_TransferCompleted()
    {
        var a = await fx.Client.NewAccountAsync(10m);
        var b = await fx.Client.NewAccountAsync();
        await fx.Client.PostAsJsonAsync("/transfers", new TransferRequest(a, b, 1m));

        var message = await Api.EventuallyAsync(async () =>
        {
            OutboxMessage? found = null;
            await fx.WithDb<AccountsDbContext>(async db =>
            {
                var rows = await db.Outbox.AsNoTracking()
                    .Where(m => m.Type == "TransferCompleted" && m.PublishedAt != null).ToListAsync();
                found = rows.FirstOrDefault(m =>
                {
                    var p = JsonDocument.Parse(m.Payload).RootElement;
                    return p.GetProperty("fromAccountId").GetInt64() == a && p.GetProperty("toAccountId").GetInt64() == b;
                });
            });
            return found;
        });

        Assert.NotNull(message.PublishedAt);
    }

    // Two competing publishers must deliver every message exactly once (FOR UPDATE SKIP LOCKED).
    [Fact]
    public async Task Two_publishers_do_not_duplicate_messages()
    {
        var control = fx.Factory.Services.GetRequiredService<OutboxControl>();
        control.Paused = true; // stop the hosted publishers so only ours compete
        try
        {
            await Task.Delay(1000); // let an in-flight batch finish

            await fx.WithDb<AccountsDbContext>(async db =>
            {
                for (var i = 0; i < 500; i++)
                    db.Outbox.Add(OutboxMessage.Create("dup-test", new { n = i }));
                await db.SaveChangesAsync();
            });
            var expected = new List<long>();
            await fx.WithDb<AccountsDbContext>(async db =>
                expected.AddRange(await db.Outbox.Where(m => m.Type == "dup-test").Select(m => m.Id).ToListAsync()));

            var sink = new CollectingSink();
            var scopes = fx.Factory.Services.GetRequiredService<IServiceScopeFactory>();
            var p1 = new OutboxPublisher<AccountsDbContext>(scopes, sink, control, NullLogger<OutboxPublisher<AccountsDbContext>>.Instance);
            var p2 = new OutboxPublisher<AccountsDbContext>(scopes, sink, control, NullLogger<OutboxPublisher<AccountsDbContext>>.Instance);

            async Task Drain(OutboxPublisher<AccountsDbContext> p)
            {
                while (await p.PublishBatchAsync(CancellationToken.None) > 0) { }
            }
            await Task.WhenAll(Drain(p1), Drain(p2));

            var delivered = sink.Ids.ToList();
            Assert.Equal(delivered.Count, delivered.Distinct().Count());           // no duplicates
            Assert.Equal(expected.Order().ToList(), delivered.Order().ToList());   // nothing lost
        }
        finally
        {
            control.Paused = false;
        }
    }

    private sealed class CollectingSink : IMessageSink
    {
        public ConcurrentBag<long> Ids { get; } = [];

        public async Task PublishAsync(IReadOnlyList<OutboxMessage> batch, CancellationToken ct)
        {
            await Task.Delay(20, ct); // widen the window in which both publishers hold batches
            foreach (var m in batch.Where(m => m.Type == "dup-test"))
                Ids.Add(m.Id);
        }
    }
}