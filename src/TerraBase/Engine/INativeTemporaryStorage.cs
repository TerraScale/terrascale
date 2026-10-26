namespace TerraBase.Engine;

/// <summary>Initializes process-wide native temporary storage before native workers start.</summary>
public interface INativeTemporaryStorage
{
    /// <summary>Initializes once per process while the engine holds its data-directory lock.</summary>
    void Initialize();
}
