namespace SimpleBroker.Platform.Outbox;

using Microsoft.EntityFrameworkCore;

public static class OutboxModelExtensions
{
    /// <summary>Call from OnModelCreating of a module context: adds the "outbox" table to the module's schema.</summary>
    public static ModelBuilder AddOutbox(this ModelBuilder mb)
    {
        mb.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
            e.Property(x => x.Type).HasColumnName("type").HasColumnType("text").IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            e.HasIndex(x => x.Id).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_pending");
        });
        return mb;
    }
}
