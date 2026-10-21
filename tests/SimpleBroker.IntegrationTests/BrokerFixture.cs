using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SimpleBroker.Accounts.Internal;
using SimpleBroker.Orders.Internal;
using SimpleBroker.Trades.Internal;
using Testcontainers.PostgreSql;
using Xunit;

namespace SimpleBroker.IntegrationTests;

/// <summary>One real Postgres per test run; tables are created from the EF model (no migrations needed).</summary>
public sealed class BrokerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:17").Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        // Env vars are the one override that Program.cs sees before building the host.
        Environment.SetEnvironmentVariable("ConnectionStrings__Broker", _pg.GetConnectionString());
        Environment.SetEnvironmentVariable("OTEL_SDK_DISABLED", "true");

        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();

        await CreateTables<AccountsDbContext>(AccountsDbContext.Schema);
        await CreateTables<OrdersDbContext>(OrdersDbContext.Schema);
        await CreateTables<TradesDbContext>(TradesDbContext.Schema);
    }

    public async Task WithDb<T>(Func<T, Task> action) where T : DbContext
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<T>();
        await action(db);
    }

    private Task CreateTables<T>(string schema) where T : DbContext =>
        WithDb<T>(async db =>
        {
            await db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA IF NOT EXISTS {schema}");
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        });

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _pg.DisposeAsync();
    }
}

[CollectionDefinition("broker")]
public sealed class BrokerCollection : ICollectionFixture<BrokerFixture>;
