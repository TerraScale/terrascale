using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.E2E
{
    /// <summary>
    /// A lane another file adds to the vs-Turso benchmark without editing it: every non-abstract implementation in this assembly
    /// is created with its parameterless constructor and runs when its <see cref="Name"/> is selected
    /// (<c>TERRABASE_VS_TURSO_LANES</c>; all of them by default). Its target is disposed after the last cell.
    /// </summary>
    internal interface IVsTursoLane
    {
        /// <summary>The lane's name in the output; unique, not one of the built-in lanes.</summary>
        string Name { get; }

        /// <summary>Starts the lane against the benchmark's single node, before the preload.</summary>
        Task<ITarget> StartAsync(VsTursoNode node, CancellationToken cancellationToken);
    }

    /// <summary>
    /// The single node the TerraBase lanes run on: its server (<see cref="ManagementApi.Server"/>: services, client address,
    /// pgwire port), the bench database and its collection, a project token with document scopes, and a scratch directory.
    /// </summary>
    internal sealed record VsTursoNode(ManagementApi Api, TenantContext Database, string Token, string Collection, string Directory);
}
