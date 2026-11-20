using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Nodus.Api.Authentication;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Nodes;
using Nodus.Api.Plans;

namespace Nodus.Tests.Integration;

public sealed class StorageKeyTests
{
    [Fact]
    public void Every_mapped_enum_uses_snake_case_and_round_trips_including_nullable_values()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>();
        DatabaseContextFactory.Configure(options, "Host=localhost;Database=unused");
        using var db = new DatabaseContext(options.Options);
        foreach (var property in db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            if (!type.IsEnum) continue;
            var converter = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter>(
                property.GetTypeMapping().Converter);
            foreach (var value in Enum.GetValues(type))
            {
                var key = JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()!);
                Assert.Equal(key, converter.ConvertToProvider(value));
                Assert.Equal(value, converter.ConvertFromProvider(key));
            }
            if (Nullable.GetUnderlyingType(property.ClrType) is not null)
                Assert.Null(converter.ConvertToProvider(null));
        }
    }

    [Fact]
    public async Task Migration_backfills_existing_keys_and_preserves_user_content()
    {
        await using var app = new TestApp();
        await app.InitializeAsync();
        await app.WithDbAsync(async db =>
        {
            var id = Guid.NewGuid();
            db.Users.Add(new User { Id = id, Plan = UserPlan.Plus, PlanSource = PlanSource.Manual,
                DisplayName = "Keep My CASE", CreatedAt = app.Time.GetUtcNow(), UpdatedAt = app.Time.GetUtcNow() });
            var nodeId = Guid.NewGuid();
            db.Nodes.Add(new Node { Id = nodeId, OwnerId = id, Type = NodeType.OneTime, Title = "My Title",
                Timezone = "Europe/Moscow", CreatedAt = app.Time.GetUtcNow(), UpdatedAt = app.Time.GetUtcNow(), UpdatedBy = id });
            await db.SaveChangesAsync();
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261018174731_AppConfig");
            Assert.Equal("Plus", await db.Database.SqlQuery<string>($"SELECT plan AS \"Value\" FROM users WHERE id = {id}").SingleAsync());
            Assert.Equal("OneTime", await db.Database.SqlQuery<string>($"SELECT type AS \"Value\" FROM nodes WHERE id = {nodeId}").SingleAsync());
            await migrator.MigrateAsync();
            Assert.Equal("plus", await db.Database.SqlQuery<string>($"SELECT plan AS \"Value\" FROM users WHERE id = {id}").SingleAsync());
            Assert.Equal("manual", await db.Database.SqlQuery<string>($"SELECT plan_source AS \"Value\" FROM users WHERE id = {id}").SingleAsync());
            Assert.Equal("one_time", await db.Database.SqlQuery<string>($"SELECT type AS \"Value\" FROM nodes WHERE id = {nodeId}").SingleAsync());
            db.ChangeTracker.Clear();
            Assert.Equal("Keep My CASE", (await db.Users.SingleAsync(user => user.Id == id)).DisplayName);
            Assert.Equal("My Title", (await db.Nodes.SingleAsync(node => node.Id == nodeId)).Title);
            Assert.False(db.Database.HasPendingModelChanges());
            return 0;
        });
    }
}
