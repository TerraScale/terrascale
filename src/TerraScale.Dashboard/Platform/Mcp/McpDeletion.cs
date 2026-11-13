using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace TerraScale.Dashboard.Platform.Mcp
{
    public static class McpDeletion
    {
        public static bool IsDelete(string tool) => tool is "delete_database" or "delete_project" or "delete_saved_query" or "delete_function" or "delete_file_bucket" or "delete_file";

        public static McpStepUpRequired Required(string tool, string resource, DatabaseScope scope, string databaseId = "", string bucketId = "")
        {
            if (string.IsNullOrWhiteSpace(resource))
            {
                throw new AccountAccessException("The resource to delete is required.", 422);
            }

            var path = tool switch
            {
                "delete_database" => "/databases/" + Uri.EscapeDataString(resource) + "/danger?delete=1",
                "delete_project" => "/organization?tab=projects",
                "delete_saved_query" => databaseId.Length == 0 ? "/databases" : "/databases/" + Uri.EscapeDataString(databaseId) + "/sql#saved-queries",
                "delete_function" => "/databases/" + Uri.EscapeDataString(databaseId) + "/functions?function=" + Uri.EscapeDataString(resource),
                "delete_file_bucket" => "/storage?bucket=" + Uri.EscapeDataString(resource),
                "delete_file" => "/storage?bucket=" + Uri.EscapeDataString(bucketId) + "&file=" + Uri.EscapeDataString(resource),
                _ => throw new UnreachableException()
            };
            var url = QueryHelpers.AddQueryString("https://dash.terrascale.tech" + path,
                new Dictionary<string, string?> { ["organizationId"] = scope.OrganizationId, ["projectId"] = scope.ProjectId });
            return new("step_up_required", tool, resource, url);
        }
    }

    public sealed record McpStepUpRequired(string Code, string Tool, string Resource, string ConfirmationUrl);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(McpStepUpRequired))]
    internal sealed partial class McpDeletionJson : JsonSerializerContext;
}
