using Xunit;

namespace HVO.AgentControl.Tests;

/// <summary>
/// Serializes tests that reserve a local ephemeral TCP port and then expect that
/// exact port to still be available to the runtime they start.
/// </summary>
/// <remarks>
/// Reserving a port (<c>bind(0)</c>, read the assigned port, release it) and
/// later using that port cannot be atomic across processes. When such tests run
/// concurrently, a second test's binder can be handed the just-released port,
/// so the runtime under test either fails to bind or talks to the wrong
/// listener. Opting this collection out of parallelism guarantees these tests
/// never overlap any other test.
/// </remarks>
[CollectionDefinition(LocalPortBindingCollection.Name, DisableParallelization = true)]
public sealed class LocalPortBindingCollection
{
    public const string Name = "local-port-binding";
}
