using System.Text.Json;
using HVO.AgentControl.Core;
using HVO.AgentControl.OpenCode;
using Xunit;

namespace HVO.AgentControl.Tests;

public sealed class ModelContextObservationTests
{
    [Fact]
    public void CatalogPreservesLimitsIdentityAndQualityProvenance()
    {
        var result = OpenCodeModelCatalog.Parse(JsonSerializer.Deserialize<JsonElement>(
            "{\"connected\":[\"openai\",\"unknown-provider\"],\"all\":[{\"id\":\"openai\",\"models\":{\"luna\":{\"name\":\"Luna\",\"limit\":{\"context\":400000,\"input\":272000,\"output\":32000}}}},{\"id\":\"unknown-provider\",\"models\":{\"mystery\":{\"limit\":{\"context\":10}}}}]}")!, "/control", 1234);

        Assert.Equal(2, result.Models.Count);
        var model = result.Models.Single(x => x.ProviderId == "openai");
        Assert.Equal(400000, model.Limits!.ContextTokens);
        Assert.Equal("Verified", model.Limits.Quality);
        Assert.Equal(ModelCatalogVersions.Schema, result.Observation.SchemaVersion);
        Assert.Equal("/control", result.Observation.Directory);
        Assert.Equal(1234, result.Observation.ObservedAt);
        Assert.Equal("Partial", result.Observation.Quality);
        Assert.Contains("advertised metadata", result.Observation.Provenance);
    }

    [Fact]
    public void MissingLimitsRemainUnknownRatherThanInvented()
    {
        var result = OpenCodeModelCatalog.Parse(JsonSerializer.SerializeToElement(new
        {
            connected = new[] { "openai" },
            all = new[] { new { id = "openai", models = new { luna = new { name = "Luna" } } } }
        }), "/work", 99);

        var limit = Assert.Single(result.Observation.Models);
        Assert.Null(limit.ContextTokens);
        Assert.Null(limit.InputTokens);
        Assert.Null(limit.OutputTokens);
        Assert.Equal("Unknown", limit.Quality);
        Assert.Equal("Unknown", result.Observation.Quality);
    }
}
