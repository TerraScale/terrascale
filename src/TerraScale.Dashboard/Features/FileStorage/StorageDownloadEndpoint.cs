using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed class StorageDownloadEndpoint(DashboardSessions sessions, AccountService accounts, FileStorageService files) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/storage/download");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FileStorageEndpoints.MutateAsync(HttpContext, sessions, accounts, files, "download"));
    }
}
