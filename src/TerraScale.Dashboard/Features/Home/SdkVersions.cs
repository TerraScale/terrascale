namespace TerraScale.Dashboard.Features.Home
{
    // Install pins for the Connect snippets; keep equal to sdk-versions.json, the dashboard/docs contract.
    public static class SdkVersions
    {
        // npm latest; its database handle carries the current SQL API (execute/query/batch).
        public const string JavaScript = "0.0.17";
        // Latest sdk/go module tag; verified to contain the SQL API (Database.Execute/Query/Batch).
        public const string Go = "v0.0.15";
        // Git ref for the pip install; no PyPI release or sdk/python tag exists before go-live (docs/decisions/go-live.md).
        public const string PythonRef = "main";
    }
}
