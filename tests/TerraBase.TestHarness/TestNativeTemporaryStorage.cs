using TerraBase.Engine;

namespace TerraBase.Testing;

// Parallel in-process hosts own different volumes; only a dedicated child tests native process state.
internal sealed class TestNativeTemporaryStorage : INativeTemporaryStorage
{
    public void Initialize() { }
}
