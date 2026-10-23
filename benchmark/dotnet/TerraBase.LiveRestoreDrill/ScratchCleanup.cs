using TerraBase.Engine.Turso;

namespace TerraBase.LiveRestoreDrill
{
    internal static class ScratchCleanup
    {
        internal static async Task DeleteAsync(TursoStorageEngine? engine, string? scratch)
        {
            if (engine is not null)
            {
                try
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    // DisposeAsync can exhaust its grace while native cleanup still owns the directory.
                    await engine.CleanupCompletion.ConfigureAwait(false);
                }
            }

            if (scratch is not null)
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }
}
