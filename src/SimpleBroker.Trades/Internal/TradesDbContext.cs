using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleBroker.Trades.Internal;

public sealed class TradesDbContext(DbContextOptions<TradesDbContext> options) : DbContext(options)
{
    public const string Schema = "trades";

    public DbSet<Trade> Trades => Set<Trade>();

    public static void Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema(Schema);

        // Only the primary key on purpose: indexes are the subject of stage 3.
        mb.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.InstrumentId).HasColumnName("instrument_id");
            e.Property(x => x.BuyOrderId).HasColumnName("buy_order_id");
            e.Property(x => x.SellOrderId).HasColumnName("sell_order_id");
            e.Property(x => x.BuyAccountId).HasColumnName("buy_account_id");
            e.Property(x => x.SellAccountId).HasColumnName("sell_account_id");
            e.Property(x => x.Price).HasColumnName("price").HasColumnType("numeric(28,8)");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(28,8)");
            e.Property(x => x.ExecutedAt).HasColumnName("executed_at").HasColumnType("timestamptz");
        });
    }
}

public sealed class TradesDesignTimeFactory : IDesignTimeDbContextFactory<TradesDbContext>
{
    public TradesDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Broker")
                 ?? "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker";
        var b = new DbContextOptionsBuilder<TradesDbContext>();
        TradesDbContext.Configure(b, cs);
        return new TradesDbContext(b.Options);
    }
}
