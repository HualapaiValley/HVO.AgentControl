using HVO.AgentControl.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class MigrationHistoryTests
{
    [Fact]
    public async Task AdditiveMigrationsRetainEntitiesIntroducedByEarlierMigrations()
    {
        await using var app = new TestApp();
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ControlDb>>().CreateDbContextAsync();
        var assembly = db.GetService<IMigrationsAssembly>();
        var prior = new HashSet<string>();
        foreach (var entry in assembly.Migrations.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var migration = assembly.CreateMigration(entry.Value, db.Database.ProviderName!);
            var current = migration.TargetModel.GetEntityTypes().Select(x => x.Name).ToHashSet();
            // This repository's migrations are additive so far. A future intentional table
            // retirement must replace this invariant with explicit retirement evidence.
            var missing = prior.Except(current).ToArray();
            Assert.True(missing.Length == 0, entry.Key + " omits earlier entities: " + string.Join(", ", missing));
            prior = current;
        }
        var snapshot = assembly.ModelSnapshot!.Model.GetEntityTypes().Select(x => x.Name).ToHashSet();
        Assert.Empty(prior.Except(snapshot));
    }
}
