using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed class StoragePageEndpoint(DashboardSessions sessions, AccountService accounts, FileStorageService files) : EndpointWithoutRequest<IResult>
    {
        public override void Configure()
        {
            Verbs("GET", "HEAD");
            Route("/storage");
        }

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FileStorageEndpoints.PageAsync(HttpContext, sessions, accounts, files));
    }
}
