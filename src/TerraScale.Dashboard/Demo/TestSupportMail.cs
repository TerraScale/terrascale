using System.Collections.Concurrent;
using TerraScale.Dashboard.Features.Support;

namespace TerraScale.Dashboard.Demo;

// Test hosts never mail the support mailbox; journeys read what would have been sent.
sealed class TestSupportMail : ISupportMail
{
    public ConcurrentQueue<SupportRequest> Sent { get; } = new();

    public Task<string> SendAsync(SupportRequest request, CancellationToken cancellationToken)
    {
        Sent.Enqueue(request);
        return Task.FromResult("accepted");
    }
}
