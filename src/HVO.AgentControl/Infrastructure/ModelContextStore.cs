using HVO.AgentControl.Core;
using Microsoft.EntityFrameworkCore;

namespace HVO.AgentControl.Infrastructure;

public sealed partial class ControlStore
{
    public Task<ModelCatalogObservation?> ModelCatalog(string runtimeId) => Read(async db =>
    {
        var record = await db.ModelCatalogObservations.AsNoTracking().SingleOrDefaultAsync(x => x.RuntimeId == runtimeId);
        return record is null ? null : Json.Read<ModelCatalogObservation>(record.Json);
    });
}
