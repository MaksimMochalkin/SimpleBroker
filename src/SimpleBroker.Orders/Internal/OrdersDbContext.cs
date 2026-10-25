using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace SimpleBroker.Orders.Internal;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<Order> Orders => Set<Order>();

    public static void Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema(Schema);

        mb.Entity<Instrument>(e =>
        {
            e.ToTable("instruments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            e.Property(x => x.Symbol).HasColumnName("symbol").HasColumnType("text").IsRequired();
            e.Property(x => x.TickSize).HasColumnName("tick_size").HasColumnType("numeric(28,8)");
        });

        mb.Entity<Order>(e =>
        {
            e.ToTable("orders");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.InstrumentId).HasColumnName("instrument_id");
            e.Property(x => x.Side).HasColumnName("side").HasColumnType("text").HasConversion<string>();
            e.Property(x => x.Type).HasColumnName("type").HasColumnType("text").HasConversion<string>();
            e.Property(x => x.Price).HasColumnName("price").HasColumnType("numeric(28,8)");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(28,8)");
            e.Property(x => x.FilledQuantity).HasColumnName("filled_quantity").HasColumnType("numeric(28,8)");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("text").HasConversion<string>();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });
    }
}

public sealed class OrdersDesignTimeFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Broker")
                 ?? "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker";
        var b = new DbContextOptionsBuilder<OrdersDbContext>();
        OrdersDbContext.Configure(b, cs);
        return new OrdersDbContext(b.Options);
    }
}
