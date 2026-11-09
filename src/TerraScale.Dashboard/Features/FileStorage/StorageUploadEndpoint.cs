using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.FileStorage
{
    public sealed class StorageUploadEndpoint(DashboardSessions sessions, AccountService accounts, FileStorageService files) : EndpointWithoutRequest<IResult>
    {
        public override void Configure() => Post("/api/v1/dashboard/storage/upload");

        public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
            new(FileStorageEndpoints.MutateAsync(HttpContext, sessions, accounts, files, "upload"));
    }
}
