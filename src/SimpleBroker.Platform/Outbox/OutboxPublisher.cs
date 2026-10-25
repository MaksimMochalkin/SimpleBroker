namespace SimpleBroker.Platform.Outbox;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public interface IMessageSink
{
    Task PublishAsync(IReadOnlyList<OutboxMessage> batch, CancellationToken ct);
}

/// <summary>Stub receiver until stage 9 (Kafka/RabbitMQ).</summary>
public sealed class LoggingMessageSink(ILogger<LoggingMessageSink> log) : IMessageSink
{
    public Task PublishAsync(IReadOnlyList<OutboxMessage> batch, CancellationToken ct)
    {
        foreach (var m in batch)
            log.LogInformation("outbox {Id} {Type} {Payload}", m.Id, m.Type, m.Payload);
        return Task.CompletedTask;
    }
}

/// <summary>Lets tests pause the background publishers.</summary>
public sealed class OutboxControl
{
    public volatile bool Paused;
}

/// <summary>
/// Polls the module's outbox table. Delivery is at-least-once: if the sink succeeds but commit fails,
/// the batch is sent again. Several instances may run in parallel (SKIP LOCKED); order across them is not guaranteed.
/// </summary>
public sealed class OutboxPublisher<TContext>(
    IServiceScopeFactory scopes, IMessageSink sink, OutboxControl control, ILogger<OutboxPublisher<TContext>> log)
    : BackgroundService where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (control.Paused || await PublishBatchAsync(ct) == 0)
                    await Task.Delay(control.Paused ? 100 : 200, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception e)
            {
                log.LogError(e, "outbox batch failed");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>One batch; public so tests can drive publishers directly. Returns the number of messages sent.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        // Use exactly the table EF itself writes to (schema + name from the model), not a guessed "<schema>.outbox".
        var entity = db.Model.FindEntityType(typeof(OutboxMessage))!;
        var schema = entity.GetSchema() ?? "";
        var table = db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

        await using var tx = await db.Database.BeginTransactionAsync(ct);
#pragma warning disable EF1002 // schema is a constant of our own module, identifiers can't be parameterized
        var batch = await db.Set<OutboxMessage>().FromSqlRaw(
            $"SELECT * FROM {table} WHERE published_at IS NULL ORDER BY id LIMIT 100 FOR UPDATE SKIP LOCKED")
            .ToListAsync(ct);
#pragma warning restore EF1002
        if (batch.Count == 0) return 0;

        await sink.PublishAsync(batch, ct); // failure here = rollback, the batch will be sent again

        var now = DateTime.UtcNow;
        foreach (var m in batch)
        {
            m.PublishedAt = now;
            PlatformMetrics.OutboxLag.Record((now - m.CreatedAt).TotalSeconds);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        PlatformMetrics.OutboxPublished.Add(batch.Count, new KeyValuePair<string, object?>("schema", schema));
        return batch.Count;
    }
}

public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddOutboxPublisher<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.TryAddSingleton<OutboxControl>();
        services.TryAddSingleton<IMessageSink, LoggingMessageSink>();
        return services.AddHostedService<OutboxPublisher<TContext>>();
    }
}
