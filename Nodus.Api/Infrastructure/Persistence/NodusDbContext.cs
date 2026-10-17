using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;

namespace Nodus.Api.Infrastructure.Persistence;

public sealed class NodusDbContext(DbContextOptions<NodusDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeRevision> NodeRevisions => Set<NodeRevision>();
    public DbSet<NodeOccurrenceState> NodeOccurrenceStates => Set<NodeOccurrenceState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        b.Entity<Node>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Title).HasMaxLength(Limits.TitleLength);
            e.Property(x => x.Description).HasMaxLength(Limits.DescriptionLength);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.Property(x => x.Recurrence).HasColumnType("jsonb").HasConversion(RuleConverter, NullableRuleComparer);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.OwnerId);
        });

        b.Entity<NodeRevision>(e =>
        {
            e.HasKey(x => new { x.NodeId, x.Revision });
            e.Property(x => x.Title).HasMaxLength(Limits.TitleLength);
            e.Property(x => x.Description).HasMaxLength(Limits.DescriptionLength);
            e.Property(x => x.Timezone).HasMaxLength(64);
            e.Property(x => x.Recurrence).HasColumnType("jsonb").HasConversion(RevisionRuleConverter, RuleComparer);
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<NodeOccurrenceState>(e =>
        {
            e.HasKey(x => new { x.NodeId, x.OccurrenceKey });
            e.Property(x => x.OccurrenceKey).HasMaxLength(32);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(Limits.TitleLength);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static readonly ValueConverter<RecurrenceRule?, string?> RuleConverter = new(
        v => v == null ? null : JsonSerializer.Serialize(v, NodusJson.Options),
        v => v == null ? null : JsonSerializer.Deserialize<RecurrenceRule>(v, NodusJson.Options));

    private static readonly ValueConverter<RecurrenceRule, string> RevisionRuleConverter = new(
        v => JsonSerializer.Serialize(v, NodusJson.Options),
        v => JsonSerializer.Deserialize<RecurrenceRule>(v, NodusJson.Options)!);

    private static readonly ValueComparer<RecurrenceRule?> NullableRuleComparer = new(
        (a, b) => Equals(a, b), v => v == null ? 0 : v.GetHashCode(), v => v);

    private static readonly ValueComparer<RecurrenceRule> RuleComparer = new(
        (a, b) => Equals(a, b), v => v.GetHashCode(), v => v);
}

/// <summary>Used by <c>dotnet ef</c>; the connection string is only needed to pick the provider.</summary>
public sealed class NodusDbContextFactory : IDesignTimeDbContextFactory<NodusDbContext>
{
    public NodusDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<NodusDbContext>();
        Configure(builder, "Host=localhost;Port=5432;Database=nodus;Username=nodus;Password=nodus");
        return new NodusDbContext(builder.Options);
    }

    public static void Configure(DbContextOptionsBuilder builder, string connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
