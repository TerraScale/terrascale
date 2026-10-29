using Microsoft.Extensions.DependencyInjection;
using TerraBase.Features.Documents;

namespace TerraBase.E2E
{
    internal static class BenchmarkSetup
    {
        public const string DatabaseName = "bench";

        public static Task<string> KeyAsync(VsTursoNode node)
        {
            return node.Api.Server.IssueNarrowedAsync(node.Database.OrganizationId, node.Database.ProjectId, null, "documents:read", "documents:write");
        }

        public static ITarget Engine(VsTursoNode node)
        {
            return new EngineTarget(node.Api.Server.Services.GetRequiredService<IDocumentService>(), node.Database, node.Collection);
        }
    }
}
