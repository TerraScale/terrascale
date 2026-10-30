using System.Globalization;
using System.Text;

namespace TerraScale.TerraBase.Rpc
{
    /// <summary>Builds REST paths with escaped segments and query values.</summary>
    public static class RestPaths
    {
        /// <summary><c>/v1/projects/{project}</c>.</summary>
        public static string Project(string projectId)
        {
            return "/v1/projects/" + Uri.EscapeDataString(projectId);
        }

        /// <summary><c>/v1/projects/{project}/databases/{database}</c>.</summary>
        public static string Database(string projectId, string database)
        {
            return Project(projectId) + "/databases/" + Uri.EscapeDataString(database);
        }

        /// <summary><c>…/collections/{collection}</c>.</summary>
        public static string Collection(string projectId, string database, string collection)
        {
            return Database(projectId, database) + "/collections/" + Uri.EscapeDataString(collection);
        }

        /// <summary><c>…/documents/by-key?id=</c>.</summary>
        public static string Document(string projectId, string database, string collection, string id)
        {
            return Collection(projectId, database, collection) + "/documents/by-key" + Query(("id", id));
        }

        /// <summary>An invariant number, or null.</summary>
        public static string? Number(long? value)
        {
            return value?.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>A query string of the non-null parameters, or empty.</summary>
        public static string Query(params ReadOnlySpan<(string Name, string? Value)> parameters)
        {
            var query = new StringBuilder();
            foreach (var (name, value) in parameters)
            {
                if (value is null)
                {
                    continue;
                }

                query.Append(query.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }

            return query.ToString();
        }
    }
}
