namespace SimpleBroker.IntegrationTests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

using SimpleBroker.Accounts.Internal;
using SimpleBroker.Orders.Internal;
using SimpleBroker.Platform.Outbox;

using Xunit;

/// <summary>Guards against a module forgetting <c>mb.AddOutbox()</c>: EF would then map the table by convention ("Outbox", PascalCase columns).</summary>
[Collection("broker")]
public class OutboxMappingTests(BrokerFixture fx)
{
    [Fact]
    public Task Accounts_outbox_is_mapped_to_accounts_outbox() => Check<AccountsDbContext>("accounts");

    [Fact]
    public Task Orders_outbox_is_mapped_to_orders_outbox() => Check<OrdersDbContext>("orders");

    private Task Check<T>(string schema) where T : DbContext => fx.WithDb<T>(db =>
    {
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        Assert.Equal(schema, entity.GetSchema());
        Assert.Equal("outbox", entity.GetTableName());
        var column = entity.FindProperty(nameof(OutboxMessage.PublishedAt))!
            .GetColumnName(StoreObjectIdentifier.Table("outbox", schema));
        Assert.Equal("published_at", column);
        return Task.CompletedTask;
    });
}
