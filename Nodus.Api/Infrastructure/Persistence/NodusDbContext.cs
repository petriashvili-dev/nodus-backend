using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Connections;
using Nodus.Api.Devices;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Nodes;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;
using Nodus.Api.Sharing;
using Nodus.Api.Sync;

namespace Nodus.Api.Infrastructure.Persistence;

public sealed class NodusDbContext(DbContextOptions<NodusDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailVerificationCode> EmailVerificationCodes => Set<EmailVerificationCode>();
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<NodeRevision> NodeRevisions => Set<NodeRevision>();
    public DbSet<NodeOccurrenceState> NodeOccurrenceStates => Set<NodeOccurrenceState>();
    public DbSet<SyncChange> SyncChanges => Set<SyncChange>();
    public DbSet<ClientOperation> ClientOperations => Set<ClientOperation>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<ConnectionInvite> ConnectionInvites => Set<ConnectionInvite>();
    public DbSet<SharedNodeMember> SharedNodeMembers => Set<SharedNodeMember>();

    /// <summary>PostgreSQL <c>timestamptz</c> accepts only UTC offsets; client-supplied instants may carry any offset.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcConverter>();
    }

    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.HasIndex(x => x.Email).IsUnique().HasFilter("email IS NOT NULL");
            e.Property(x => x.DisplayName).HasMaxLength(UserProfile.DisplayNameLength);
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => x.UserId);
            e.Property(x => x.RevokedReason).HasConversion<string>().HasMaxLength(32);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailVerificationCode>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => new { x.Email, x.CreatedAt });
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

        b.Entity<SyncChange>(e =>
        {
            e.HasKey(x => new { x.RecipientUserId, x.Revision });
            e.Property(x => x.EntityType).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EntityId).HasMaxLength(80);
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClientOperation>(e =>
        {
            e.HasKey(x => new { x.UserId, x.IdempotencyKey });
            e.Property(x => x.IdempotencyKey).HasMaxLength(Limits.IdempotencyKeyLength);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            // text, not jsonb: replays must be byte-identical and jsonb reorders keys.
            e.Property(x => x.ResponseBody).HasColumnType("text");
            e.HasIndex(x => x.CreatedAt);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Device>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.InstallationId).HasMaxLength(64);
            e.Property(x => x.Platform).HasMaxLength(16);
            e.Property(x => x.ApnsToken).HasMaxLength(200);
            e.Property(x => x.ApnsEnvironment).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AppVersion).HasMaxLength(32);
            e.HasIndex(x => new { x.UserId, x.InstallationId }).IsUnique();
            e.HasIndex(x => x.ApnsToken).IsUnique().HasFilter("apns_token IS NOT NULL");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Connection>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserLowId, x.UserHighId }).IsUnique();
            e.HasIndex(x => x.UserHighId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.ToTable(t => t.HasCheckConstraint("ck_connections_normalized_pair", "user_low_id < user_high_id"));
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserLowId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserHighId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ConnectionInvite>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.CodeHash);
            e.HasIndex(x => new { x.InviterId, x.Status });
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.InviterId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SharedNodeMember>(e =>
        {
            e.HasKey(x => new { x.NodeId, x.UserId });
            e.HasIndex(x => x.UserId);
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OutboxEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.LastError).HasMaxLength(200);
            e.HasIndex(x => x.Id).HasFilter("processed_at IS NULL").HasDatabaseName("ix_outbox_events_pending");
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
