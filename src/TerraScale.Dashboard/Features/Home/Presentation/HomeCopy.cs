namespace TerraScale.Dashboard.Features.Home.Presentation;

/// <summary>1:1 copy of src/features/home/home-copy.ts (en).</summary>
public static class HomeCopy
{
    public static readonly HomeCopyEn En = new();

    public sealed record HomeCopyEn
    {
        public string CreateFirstDatabase => "Create first database";
        public string CreateCredential => "Create an Application credential";
        public string Databases => "Databases";
        public string DatabaseDetail(int count) => count == 0 ? "none saved" : "saved project databases";
        public string UnreadAlerts => "Unread alerts";
        public string NotificationsLoaded(int count) => $"{count} notifications loaded";
        public string WorkspaceMetrics => "Workspace metrics";
        public string StaleTitle => "Monitoring snapshot may be stale";
        public string StaleBody => "Some monitoring sources are temporarily unavailable. The last successful project data remains visible.";
        public string Refresh => "Refresh snapshot";
        public string Days30 => "30 days";
        public string CurrentSnapshot => "Current snapshot";
        public string ActivityTitle => "Activity";
        public string ActivitySubtitle => "Persisted project audit activity";
        public string ActivityChart(int count) => $"Project activity chart with {count} time buckets";
        public string GettingStarted => "Getting started";
        public string GettingStartedSubtitle => "Provision your project and connect the tools you already use";
        public string GettingStartedIntro => "Work through these steps to get a database online and wire it into your stack.";
        public string StepDatabaseBody => "Create a logical database in the region closest to your users.";
        public string StepDatabaseCta => "Open databases";
        public string StepCredentialBody => "Generate a scoped API key or M2M client for your application.";
        public string StepCredentialCta => "Open API keys";
        public string ConnectTitle => "Connect your AI or platform";
        public string ConnectSubtitle => "Reach this project from your assistant, application, or pipeline. Connection flows are previewed while provisioning finishes.";
        public string ConnectSoon => "Preview";
        public string ConnectMcpTitle => "AI assistants";
        public string ConnectMcpBody => "Connect Claude, Cursor, and other MCP-compatible assistants to your project.";
        public string ConnectSdkTitle => "TypeScript SDK";
        public string ConnectSdkBody => "Install the SDK and query your databases directly from your application.";
        public string ConnectApiTitle => "REST API";
        public string ConnectApiBody => "Call the project API from any language with a scoped credential.";
        public string ConnectCliTitle => "CLI and CI";
        public string ConnectCliBody => "Manage databases and run queries from your terminal or pipeline.";
        public string ConnectSnippetLabel => "MCP client configuration";
        public string ConnectSnippet => """
            {
              "mcpServers": {
                "terrascale": {
                  "url": "https://<your-terrascale-host>/mcp"
                }
              }
            }
            """;
        public string Done => "Done";
        public string Open => "Open";
        public string Events => "Events";
        public string NoActivity => "No activity recorded in this range yet.";
    }
}
