using System.Text.Json;
using HVO.AgentControl.Core;

namespace HVO.AgentControl.OpenCode;

public sealed record OpenCodeModelCatalogResult(IReadOnlyList<ModelChoice> Models, ModelCatalogObservation Observation);

public static class OpenCodeModelCatalog
{
    public static OpenCodeModelCatalogResult Parse(JsonElement providers, string directory, long observedAt)
    {
        var connected = providers.TryGetProperty("connected", out var connectedJson) && connectedJson.ValueKind == JsonValueKind.Array
            ? connectedJson.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal) : [];
        var models = new List<ModelChoice>();
        var limits = new List<ModelLimitEntry>();
        foreach (var provider in providers.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.Array
                     ? all.EnumerateArray() : [])
        {
            if (!provider.TryGetProperty("id", out var providerValue) || providerValue.ValueKind != JsonValueKind.String) continue;
            var providerId = providerValue.GetString()!;
            if (!connected.Contains(providerId) || !provider.TryGetProperty("models", out var modelValues) || modelValues.ValueKind != JsonValueKind.Object) continue;
            foreach (var model in modelValues.EnumerateObject())
            {
                var value = model.Value;
                var name = value.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString() ?? model.Name : model.Name;
                var limit = ReadLimit(value, providerId, model.Name);
                limits.Add(limit);
                models.Add(new ModelChoice(providerId, model.Name, name,
                    value.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Object
                        ? variants.EnumerateObject().Select(x => x.Name).ToArray() : [])
                { Limits = limit });
            }
        }
        var quality = limits.Count == 0 || limits.All(x => x.Quality == "Unknown") ? "Unknown" :
            limits.All(x => x.Quality == "Verified") ? "Verified" : "Partial";
        var observation = new ModelCatalogObservation(ModelCatalogVersions.Schema, ModelCatalogVersions.Catalog,
            "opencode-provider", "opencode-http-api", directory, observedAt, quality,
            "native /provider response; model limits are advertised metadata, not provider acceptance proof.", limits);
        return new(models, observation);
    }

    private static ModelLimitEntry ReadLimit(JsonElement model, string providerId, string modelId)
    {
        if (!model.TryGetProperty("limit", out var limit) || limit.ValueKind != JsonValueKind.Object)
            return new(providerId, modelId, null, null, null, "Unknown", "No native limit object was advertised.");
        var context = Counter(limit, "context");
        var input = Counter(limit, "input");
        var output = Counter(limit, "output");
        var present = new[] { context, input, output }.Count(x => x.HasValue);
        return new(providerId, modelId, context, input, output, present == 3 ? "Verified" : present == 0 ? "Unknown" : "Partial",
            "native /provider model.limit metadata");
    }

    private static long? Counter(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number) && number >= 0 ? number : null;
}
