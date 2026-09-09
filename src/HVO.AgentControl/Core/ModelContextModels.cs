namespace HVO.AgentControl.Core;

public static class ModelCatalogVersions
{
    public const int Schema = 1;
    public const int Catalog = 1;
}

public sealed record ModelLimitEntry(string ProviderId, string ModelId, long? ContextTokens,
    long? InputTokens, long? OutputTokens, string Quality, string Provenance);

public sealed record ModelCatalogObservation(int SchemaVersion, int CatalogVersion, string Catalog,
    string Adapter, string Directory, long ObservedAt, string Quality, string Provenance,
    IReadOnlyList<ModelLimitEntry> Models);
