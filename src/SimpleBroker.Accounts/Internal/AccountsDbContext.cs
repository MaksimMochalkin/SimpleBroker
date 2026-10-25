namespace SimpleBroker.Accounts.Internal;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using SimpleBroker.Platform.Outbox;

public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public const string Schema = "accounts";

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Balance> Balances => Set<Balance>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public static void Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.HasDefaultSchema(Schema);

        mb.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.Owner).HasColumnName("owner").HasColumnType("text").IsRequired();
            e.Property(x => x.Currency).HasColumnName("currency").HasColumnType("text").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        mb.Entity<Balance>(e =>
        {
            e.ToTable("balances", t => t.HasCheckConstraint("ck_balances_nonneg", "available >= 0 AND reserved >= 0"));
            e.HasKey(x => x.AccountId);
            e.Property(x => x.AccountId).HasColumnName("account_id").ValueGeneratedNever();
            e.Property(x => x.Available).HasColumnName("available").HasColumnType("numeric(28,8)");
            e.Property(x => x.Reserved).HasColumnName("reserved").HasColumnType("numeric(28,8)");
            // Optimistic concurrency token: tracked updates become UPDATE ... WHERE version = @old.
            // ExecuteUpdate ignores it (Naive / Atomic / Deposit are not affected).
            e.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        });

        mb.Entity<Position>(e =>
        {
            e.ToTable("positions");
            e.HasKey(x => new { x.AccountId, x.InstrumentId });
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.InstrumentId).HasColumnName("instrument_id");
            e.Property(x => x.Quantity).HasColumnName("quantity").HasColumnType("numeric(28,8)");
            e.Property(x => x.AvgPrice).HasColumnName("avg_price").HasColumnType("numeric(28,8)");
        });

        mb.Entity<Reservation>(e =>
        {
            e.ToTable("reservations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();
            e.Property(x => x.AccountId).HasColumnName("account_id");
            e.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(28,8)");
            e.Property(x => x.Status).HasColumnName("status").HasColumnType("text").HasConversion<string>();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        mb.AddOutbox();
    }
}

public sealed class AccountsDesignTimeFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Broker")
                 ?? "Host=localhost;Port=5433;Username=broker;Password=broker;Database=broker";
        var b = new DbContextOptionsBuilder<AccountsDbContext>();
        AccountsDbContext.Configure(b, cs);
        return new AccountsDbContext(b.Options);
    }
}
