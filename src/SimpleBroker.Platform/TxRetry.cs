namespace SimpleBroker.Platform;

using System.Data;

using Microsoft.EntityFrameworkCore;

using Npgsql;

/// <summary>Runs work in a transaction and retries on serialization failure (40001) and deadlock (40P01).</summary>
public static class TxRetry
{
    public static PostgresException? Pg(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
            if (x is PostgresException pg) return pg; // EF may wrap the Npgsql error several levels deep
        return null;
    }

    public static bool IsRetryable(Exception e) => Pg(e)?.SqlState is "40001" or "40P01";

    public static bool IsDeadlock(Exception e) => Pg(e)?.SqlState == "40P01";

    /// <param name="max">Total attempts; 1 = no retry (used to demonstrate a raw deadlock).</param>
    public static async Task<T> RunAsync<T>(DbContext db, IsolationLevel level, Func<Task<T>> work,
        CancellationToken ct, int max = 5)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear(); // pooled context: do not carry entities over from a failed attempt
            await using var tx = await db.Database.BeginTransactionAsync(level, ct);
            try
            {
                var result = await work();
                await tx.CommitAsync(ct);
                return result;
            }
            catch (Exception e) when (IsRetryable(e) && attempt < max)
            {
                PlatformMetrics.Retries.Add(1,
                    new KeyValuePair<string, object?>("reason", IsDeadlock(e) ? "deadlock" : "serialization"));
                // leaving the iteration disposes tx = rollback
                await Task.Delay(Random.Shared.Next(5, 10 * (1 << attempt)), ct);
            }
        }
    }
}