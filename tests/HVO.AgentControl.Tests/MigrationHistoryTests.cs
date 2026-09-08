using HVO.AgentControl.Core;
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
    public async Task AdditiveMigrationsRetainEntitiesAndPropertiesIntroducedByEarlierMigrations()
    {
        await using var app = new TestApp();
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ControlDb>>().CreateDbContextAsync();
        var assembly = db.GetService<IMigrationsAssembly>();
        var prior = new HashSet<string>();
        foreach (var entry in assembly.Migrations.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var migration = assembly.CreateMigration(entry.Value, db.Database.ProviderName!);
            var current = migration.TargetModel.GetEntityTypes()
                .SelectMany(entity => entity.GetProperties().Select(property => entity.Name + "." + property.Name)).ToHashSet();
            // This repository's migrations are additive so far. A future intentional table
            // or column retirement must replace this invariant with explicit retirement evidence.
            var missing = prior.Except(current).ToArray();
            Assert.True(missing.Length == 0, entry.Key + " omits earlier mapped properties: " + string.Join(", ", missing));
            prior = current;
        }
        var snapshot = assembly.ModelSnapshot!.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties().Select(property => entity.Name + "." + property.Name)).ToHashSet();
        Assert.Empty(prior.Except(snapshot));
    }

    [Fact]
    public async Task LatestMigrationTargetSnapshotAndCurrentModelHaveTheSameSchema()
    {
        await using var app = new TestApp();
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ControlDb>>().CreateDbContextAsync();
        var assembly = db.GetService<IMigrationsAssembly>();
        var latest = assembly.Migrations.OrderBy(x => x.Key, StringComparer.Ordinal).Last();
        var migration = assembly.CreateMigration(latest.Value, db.Database.ProviderName!);
        var initializer = db.GetService<IModelRuntimeInitializer>();
        var target = initializer.Initialize(migration.TargetModel, designTime: true).GetRelationalModel();
        var snapshot = initializer.Initialize(assembly.ModelSnapshot!.Model, designTime: true).GetRelationalModel();
        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(target, snapshot);
        Assert.True(differences.Count == 0, latest.Key + " differs from the snapshot: " +
            string.Join(", ", differences.Select(operation => operation.GetType().Name)));
        Assert.False(db.Database.HasPendingModelChanges(), "The model snapshot differs from the current application model.");
    }

    [Fact]
    public async Task TaskSessionActivationSchemaPersistsOwnersIntentAndConcurrency()
    {
        await using var app = new TestApp();
        await using var db = await app.Services.GetRequiredService<IDbContextFactory<ControlDb>>().CreateDbContextAsync();
        var model = db.Model;
        var work = model.FindEntityType(typeof(WorkItem))!;
        var phase = model.FindEntityType(typeof(WorkItemPhase))!;
        var session = model.FindEntityType(typeof(TaskSessionBindingRecord))!;

        Assert.NotNull(work.FindProperty("OwnerWorkerSlotId"));
        Assert.NotNull(phase.FindProperty("OwnerWorkerSlotId"));
        Assert.NotNull(session.FindProperty("WorkerId"));
        Assert.NotNull(session.FindProperty("CreationCommandId"));
        Assert.True(session.FindProperty("Revision")!.IsConcurrencyToken);
        Assert.Contains(session.GetIndexes(), index => index.Properties.Select(x => x.Name).SequenceEqual(["CreationCommandId"]) && index.IsUnique);
    }
}
