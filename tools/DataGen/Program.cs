// DataGen: deterministic bulk loader for Simple Broker (stage 1, S9).
//   dotnet run -c Release --project tools/DataGen -- --scale S --seed 42
// Loads accounts/balances, instruments, orders, trades via binary COPY and writes loadtests/data/accounts.json.
// Skew: instruments ~ Zipf(s=1.1); accounts: 20% "active" accounts receive 80% of activity.
using System.Diagnostics;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

var opt = Options.Parse(args);
var scale = Scale.From(opt.Scale);
var rng = new Random(opt.Seed);
var anchor = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

Console.WriteLine($"scale={opt.Scale} seed={opt.Seed} {scale}");

var activeCount = Math.Max(1, scale.Accounts / 5);
var zipf = BuildZipfCdf(scale.Instruments, 1.1);

int PickInstrument()
{
    var idx = Array.BinarySearch(zipf, rng.NextDouble());
    if (idx < 0) idx = ~idx;
    return Math.Min(idx, scale.Instruments - 1) + 1;
}
long PickAccount() =>
    rng.NextDouble() < 0.8 || activeCount == scale.Accounts
        ? 1 + rng.Next(activeCount)
        : activeCount + 1 + rng.Next(scale.Accounts - activeCount);
DateTime RandomTime() => anchor.AddSeconds(-rng.NextDouble() * 365 * 86400);
decimal Round2(double v) => Math.Round((decimal)v, 2);

await using var conn = new NpgsqlConnection(opt.Connection);
await conn.OpenAsync();

var total = Stopwatch.StartNew();

await Phase("truncate", async () =>
{
    await using var cmd = new NpgsqlCommand(
        "TRUNCATE accounts.accounts, accounts.balances, accounts.positions, orders.instruments, orders.orders, trades.trades RESTART IDENTITY", conn);
    await cmd.ExecuteNonQueryAsync();
});

await Phase("instruments", () => Copy(
    "COPY orders.instruments (id, symbol, tick_size) FROM STDIN (FORMAT BINARY)", scale.Instruments, (w, i) =>
    {
        w.Write((int)i, NpgsqlDbType.Integer);
        w.Write($"SYM{i:D4}", NpgsqlDbType.Text);
        w.Write(0.01m, NpgsqlDbType.Numeric);
    }));

await Phase("accounts", () => Copy(
    "COPY accounts.accounts (id, owner, currency, created_at) FROM STDIN (FORMAT BINARY)", scale.Accounts, (w, i) =>
    {
        w.Write(i, NpgsqlDbType.Bigint);
        w.Write($"owner-{i}", NpgsqlDbType.Text);
        w.Write("USD", NpgsqlDbType.Text);
        w.Write(RandomTime(), NpgsqlDbType.TimestampTz);
    }));

await Phase("balances", () => Copy(
    "COPY accounts.balances (account_id, available, reserved, version) FROM STDIN (FORMAT BINARY)", scale.Accounts, (w, i) =>
    {
        w.Write(i, NpgsqlDbType.Bigint);
        w.Write(Round2(1000 + rng.NextDouble() * 99000), NpgsqlDbType.Numeric);
        w.Write(0m, NpgsqlDbType.Numeric);
        w.Write(0L, NpgsqlDbType.Bigint);
    }));

await Phase("orders", () => Copy(
    "COPY orders.orders (id, account_id, instrument_id, side, type, price, quantity, filled_quantity, status, created_at) FROM STDIN (FORMAT BINARY)",
    scale.Orders, (w, i) =>
    {
        var instrument = PickInstrument();
        w.Write(i, NpgsqlDbType.Bigint);
        w.Write(PickAccount(), NpgsqlDbType.Bigint);
        w.Write(instrument, NpgsqlDbType.Integer);
        w.Write(rng.Next(2) == 0 ? "Buy" : "Sell", NpgsqlDbType.Text);
        w.Write("Limit", NpgsqlDbType.Text);
        w.Write(Round2(50 + instrument % 100 + rng.NextDouble() * 10), NpgsqlDbType.Numeric);
        w.Write(Round2(1 + rng.Next(100)), NpgsqlDbType.Numeric);
        w.Write(0m, NpgsqlDbType.Numeric);
        w.Write("New", NpgsqlDbType.Text);
        w.Write(RandomTime(), NpgsqlDbType.TimestampTz);
    }));

// Note: trades are synthetic; their order/account ids are independent of orders.orders rows.
await Phase("trades", () => Copy(
    "COPY trades.trades (id, instrument_id, buy_order_id, sell_order_id, buy_account_id, sell_account_id, price, quantity, executed_at) FROM STDIN (FORMAT BINARY)",
    scale.Trades, (w, i) =>
    {
        var instrument = PickInstrument();
        w.Write(i, NpgsqlDbType.Bigint);
        w.Write(instrument, NpgsqlDbType.Integer);
        w.Write(1L + rng.NextInt64(scale.Orders), NpgsqlDbType.Bigint);
        w.Write(1L + rng.NextInt64(scale.Orders), NpgsqlDbType.Bigint);
        w.Write(PickAccount(), NpgsqlDbType.Bigint);
        w.Write(PickAccount(), NpgsqlDbType.Bigint);
        w.Write(Round2(50 + instrument % 100 + rng.NextDouble() * 10), NpgsqlDbType.Numeric);
        w.Write(Round2(1 + rng.Next(100)), NpgsqlDbType.Numeric);
        w.Write(RandomTime(), NpgsqlDbType.TimestampTz);
    }));

await Phase("sequences", async () =>
{
    foreach (var (table, seq) in new[] { ("accounts.accounts", "accounts.accounts"), ("orders.orders", "orders.orders") })
    {
        await using var cmd = new NpgsqlCommand(
            $"SELECT setval(pg_get_serial_sequence('{seq}', 'id'), (SELECT max(id) FROM {table}))", conn);
        await cmd.ExecuteScalarAsync();
    }
});

await Phase("analyze", async () =>
{
    await using var cmd = new NpgsqlCommand("ANALYZE", conn);
    cmd.CommandTimeout = 0;
    await cmd.ExecuteNonQueryAsync();
});

Directory.CreateDirectory(opt.Out);
var sample = Enumerable.Range(0, 10_000).Select(_ => 1 + rng.Next(scale.Accounts)).ToArray();
await File.WriteAllTextAsync(Path.Combine(opt.Out, "accounts.json"), JsonSerializer.Serialize(sample));
Console.WriteLine($"wrote {Path.Combine(opt.Out, "accounts.json")} ({sample.Length} ids)");
Console.WriteLine($"done in {total.Elapsed.TotalSeconds:F1}s");

// ---- helpers
async Task Phase(string name, Func<Task> body)
{
    var sw = Stopwatch.StartNew();
    Console.Write($"{name,-12}");
    await body();
    Console.WriteLine($"{sw.Elapsed.TotalSeconds,8:F1}s");
}

Task Copy(string sql, long count, Action<NpgsqlBinaryImporter, long> row) => Task.Run(() =>
{
    using var writer = conn.BeginBinaryImport(sql);
    for (long i = 1; i <= count; i++)
    {
        writer.StartRow();
        row(writer, i);
    }
    writer.Complete();
});

static double[] BuildZipfCdf(int n, double s)
{
    var cdf = new double[n];
    double sum = 0;
    for (var i = 1; i <= n; i++) sum += 1.0 / Math.Pow(i, s);
    double acc = 0;
    for (var i = 1; i <= n; i++)
    {
        acc += 1.0 / Math.Pow(i, s) / sum;
        cdf[i - 1] = acc;
    }
    cdf[n - 1] = 1.0;
    return cdf;
}

internal sealed record Scale(int Accounts, int Instruments, long Orders, long Trades)
{
    public static Scale From(string s) => s.ToUpperInvariant() switch
    {
        "S" => new Scale(10_000, 100, 500_000, 100_000),
        "M" => new Scale(100_000, 500, 5_000_000, 1_000_000),
        "L" => new Scale(1_000_000, 1_000, 50_000_000, 10_000_000),
        _ => throw new ArgumentException("--scale must be S, M or L"),
    };
}

internal sealed record Options(string Scale, int Seed, string Connection, string Out)
{
    public static Options Parse(string[] args)
    {
        string scale = "S", conn = "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker;Command Timeout=0", output = "loadtests/data";
        var seed = 42;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--scale": scale = args[++i]; break;
                case "--seed": seed = int.Parse(args[++i]); break;
                case "--connection": conn = args[++i]; break;
                case "--out": output = args[++i]; break;
            }
        }
        return new Options(scale, seed, conn, output);
    }
}
