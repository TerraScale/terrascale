using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Demo
{
    // Test-only producer for the current generated-ULID import contract on the region's client listener. Wire shapes are written here from the
    // contract, independently of the dashboard's records. It parses no SQL or SQLite:
    // a fixture marker selects a synthetic preview, so journeys exercise the dashboard.
    sealed partial class TestImports(HttpMessageHandler inner, TestDatabases databases, TestDocuments documents,
        Func<string, (string UserId, string OrganizationId, string ProjectId, IReadOnlyList<string> Scopes)?> resolveProjectToken) : DelegatingHandler(inner)
    {
        private const long Part = 4L << 20;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<string, Job> jobs = [];
        private readonly ConcurrentQueue<JsonObject> requests = new();

        private sealed class Job
        {
            public required string Id, Project, Database, Format, Sha256;
            public required long Bytes;
            public string State = "awaiting_source";
            public List<string> Exclude = [];
            public string? Collection;
            public JsonElement[] PlainDocuments = [];
            public MemoryStream Received = new();
            public bool Staged, CancelRequested, Corrupt, Manual;
            public required DatabaseScope Scope;
            public required DatabaseRegion Home;
            public string? PreviewId, ConfirmedPreviewId;
            public JsonObject? Error, Validation, Fail;
            public long Written;
            public DateTimeOffset CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow;
            public DateTimeOffset? StartedAt, FinishedAt, RetainedUntil;
            public string Fixture = "";
        }

        public void MapRoutes(WebApplication app)
        {
            app.MapGet("/__test/imports/requests", () => Results.Json(requests.ToArray()));
            // Controls: step a manual job (journeys), fail its next batch, damage its next completed upload, or lose its staged source.
            app.MapPost("/__test/imports/control", async (HttpContext http) =>
            {
                var body = await JsonNode.ParseAsync(http.Request.Body) as JsonObject ?? [];
                await gate.WaitAsync();
                try
                {
                    var job = jobs.Values.SingleOrDefault(item => item.Database == body["database"]?.GetValue<string>());
                    if (job is null)
                    {
                        return Results.NotFound();
                    }

                    switch (body["command"]?.GetValue<string>())
                    {
                        case "manual": job.Manual = true; break;
                        case "step": await AdvanceAsync(job.Scope, job.Home, job, http.RequestAborted); break;
                        case "fail": job.Fail = new() { ["code"] = body["code"]!.GetValue<string>(), ["retryable"] = body["retryable"]!.GetValue<bool>() }; break;
                        case "corrupt": job.Corrupt = true; break;
                        case "lose-source": job.Staged = false; job.Received = new(); break;
                        default: return Results.BadRequest();
                    }
                    return Results.NoContent();
                }
                finally { gate.Release(); }
            });
        }

        [GeneratedRegex("^/v1/projects/([^/]+)/databases/([^/]+)/import(?:/(source|start|cancel|resume))?$")]
        private static partial Regex Route();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var match = Route().Match(uri.AbsolutePath);
            if (!match.Success)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var token = request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } value } ? value : "";
            var verb = match.Groups[3].Value;
            requests.Enqueue(new()
            {
                ["method"] = request.Method.Method,
                ["host"] = uri.Host,
                ["route"] = verb.Length == 0 ? "import" : verb,
                ["query"] = uri.Query,
                ["bytes"] = body.Length,
                ["bearer"] = resolveProjectToken(token)?.UserId,
                ["scopes"] = resolveProjectToken(token) is { } granted ? string.Join(' ', granted.Scopes.Order(StringComparer.Ordinal)) : null,
                ["peerCredential"] = request.Headers.TryGetValues(TerraScale.TerraBase.Contracts.TerraBaseRpc.PeerCredentialHeader, out var peers) && peers.Single() == TestIdentity.PeerSecret,
                ["fields"] = request.Content?.Headers.ContentType?.MediaType == "application/json" && JsonNode.Parse(body) is JsonObject json
                    ? new JsonArray(json.Select(field => (JsonNode?)field.Key).ToArray()) : null
            });
            // Import routes are project management routes: no project token is 401, another project's is 404.
            if (resolveProjectToken(token) is not { } grant)
            {
                return Error(HttpStatusCode.Unauthorized, "unauthorized");
            }

            if (uri.Host != "gru.api.terrascale.tech")
            {
                return Error(HttpStatusCode.MisdirectedRequest, "wrong_region");
            }

            var project = Uri.UnescapeDataString(match.Groups[1].Value);
            var database = Uri.UnescapeDataString(match.Groups[2].Value);
            var scope = new DatabaseScope("", "", "", "", project, "");
            var home = (await databases.RegionsAsync(cancellationToken))[0];
            var destination = (await databases.ListAsync(scope, home, cancellationToken)).FirstOrDefault(item => item.Name == database);
            if (grant.ProjectId != project || destination is null)
            {
                return Error(HttpStatusCode.NotFound, "unknown_database");
            }
            // Each route's scopes (contract section 2): getImport reads, cancelImport writes the database, the rest also write documents.
            string[] required = (request.Method.Method, verb) switch
            {
                ("GET", "") => ["databases:read"],
                ("POST", "cancel") => ["databases:write"],
                _ => ["databases:write", "documents:write"]
            };
            if (required.Except(grant.Scopes).Any())
            {
                return Error(HttpStatusCode.Forbidden, "forbidden_scope");
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                jobs.TryGetValue(project + "/" + database, out var job);
                if (request.Method == HttpMethod.Post && verb.Length == 0)
                {
                    return destination.ReplicaRegions is { Count: > 0 } ? Error(HttpStatusCode.Conflict, "destination_replicated")
                        : await CreateAsync(scope, home, project, database, job, body, cancellationToken);
                }

                if (job is null)
                {
                    return Error(HttpStatusCode.NotFound, "unknown_import");
                }

                job.UpdatedAt = DateTimeOffset.UtcNow;
                return (request.Method.Method, verb) switch
                {
                    // The demo advances one step per read; journeys step manual jobs explicitly.
                    ("GET", "") => job.Manual ? Reply(HttpStatusCode.OK, job) : await AdvanceAsync(scope, home, job, cancellationToken),
                    ("PUT", "source") => Upload(job, uri.Query, body),
                    ("PATCH", "") => Update(job, body),
                    ("POST", "start") => Start(job, body),
                    ("POST", "cancel") => Cancel(job),
                    ("POST", "resume") => Resume(job),
                    _ => Error(HttpStatusCode.MethodNotAllowed, "invalid_request")
                };
            }
            finally { gate.Release(); }
        }

        private async Task<HttpResponseMessage> CreateAsync(DatabaseScope scope, DatabaseRegion home, string project, string database, Job? existing, byte[] body, CancellationToken ct)
        {
            if (JsonNode.Parse(body) is not JsonObject request || request["format"]?.GetValue<string>() is not ("postgres_plain" or "sqlite" or "ndjson") ||
                request["sourceBytes"]?.GetValue<long>() is not (>= 1 and <= 1L << 30) || request["sourceSha256"]?.GetValue<string>() is not { Length: 64 } sha ||
                !sha.All(char.IsAsciiHexDigitLower) || request["options"] is not JsonObject options ||
                options.Any(field => field.Key is not ("exclude" or "collection")) || options["exclude"] is not JsonArray exclude)
            {
                return Error(HttpStatusCode.BadRequest, "invalid_request");
            }

            if (existing is not null)
            {
                return existing.Format == request["format"]!.GetValue<string>() && existing.Bytes == request["sourceBytes"]!.GetValue<long>() &&
                    existing.Sha256 == sha && existing.Collection == options["collection"]?.GetValue<string>() && existing.Exclude.SequenceEqual(exclude.Select(item => item!.GetValue<string>()))
                    ? Reply(HttpStatusCode.OK, existing) : Error(HttpStatusCode.Conflict, "import_exists");
            }

            if (documents.Statistics(scope, database).Count > 0)
            {
                return Error(HttpStatusCode.Conflict, "destination_not_empty");
            }

            var job = new Job
            {
                Id = Guid.CreateVersion7().ToString("N").ToUpperInvariant()[..26],
                Project = project,
                Database = database,
                Scope = scope,
                Home = home,
                Format = request["format"]!.GetValue<string>(),
                Bytes = request["sourceBytes"]!.GetValue<long>(),
                Sha256 = sha,
                Exclude = exclude.Select(item => item!.GetValue<string>()).ToList(),
                Collection = options["collection"]?.GetValue<string>()
            };
            jobs[project + "/" + database] = job;
            await documents.CreateCollectionAsync(scope, home, database, "terrascale-import", ct);
            return Reply(HttpStatusCode.Created, job);
        }

        private HttpResponseMessage Upload(Job job, string query, byte[] part)
        {
            if (!(job.State == "awaiting_source" || job.State is "failed" or "cancelled" && !job.Staged))
            {
                return Error(HttpStatusCode.Conflict, "invalid_state");
            }

            if (!long.TryParse(Regex.Match(query, "[?&]offset=([0-9]+)").Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || part.Length == 0)
            {
                return Error(HttpStatusCode.BadRequest, "invalid_request");
            }

            if (part.Length > Part || offset + part.Length > job.Bytes)
            {
                return Error(HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
            }

            if (offset + part.Length <= job.Received.Length)
            {
                return Reply(HttpStatusCode.OK, job);
            }

            if (offset != job.Received.Length)
            {
                return Error(HttpStatusCode.Conflict, "offset_mismatch");
            }

            job.Received.Write(part);
            if (job.Received.Length < job.Bytes)
            {
                return Reply(HttpStatusCode.OK, job);
            }

            if (job.Corrupt || Convert.ToHexStringLower(SHA256.HashData(job.Received.ToArray())) != job.Sha256)
            {
                job.Corrupt = false;
                job.Received = new();
                job.State = "awaiting_source";
                job.Error = Failure("source_checksum_mismatch", "The uploaded bytes do not match the declared SHA-256.", true);
                return Reply(HttpStatusCode.OK, job);
            }
            job.Staged = true;
            job.Error = null;
            if (job.State == "awaiting_source")
            {
                job.State = "analyzing";
            }

            return Reply(HttpStatusCode.OK, job);
        }

        private HttpResponseMessage Update(Job job, byte[] body)
        {
            if (job.State is not ("ready" or "rejected"))
            {
                return Error(HttpStatusCode.Conflict, "invalid_state");
            }

            if (JsonNode.Parse(body)?["options"] is not JsonObject options || options.Any(field => field.Key is not ("exclude" or "collection")) ||
                options["exclude"] is not JsonArray exclude)
            {
                return Error(HttpStatusCode.BadRequest, "invalid_request");
            }

            job.Exclude = exclude.Select(item => item!.GetValue<string>()).ToList();
            job.Collection = options["collection"]?.GetValue<string>();
            job.State = "analyzing";
            job.PreviewId = null;
            return Reply(HttpStatusCode.Accepted, job);
        }

        private HttpResponseMessage Start(Job job, byte[] body)
        {
            var previewId = JsonNode.Parse(body)?["previewId"]?.GetValue<string>();
            if (job.State is "queued" or "running" && previewId == job.ConfirmedPreviewId)
            {
                return Reply(HttpStatusCode.Accepted, job);
            }

            if (job.State != "ready")
            {
                return Error(HttpStatusCode.Conflict, "invalid_state");
            }

            if (previewId != job.PreviewId)
            {
                return Error(HttpStatusCode.Conflict, "preview_changed");
            }

            if (Preview(job)["blocking"]!.GetValue<int>() > 0)
            {
                return Error(HttpStatusCode.Conflict, "preview_blocked");
            }

            job.ConfirmedPreviewId = previewId;
            job.State = "queued";
            job.StartedAt = DateTimeOffset.UtcNow;
            return Reply(HttpStatusCode.Accepted, job);
        }

        private HttpResponseMessage Cancel(Job job)
        {
            if (job.State == "cancelled")
            {
                return Reply(HttpStatusCode.Accepted, job);
            }

            if (job.State is "completed" or "failed")
            {
                return Error(HttpStatusCode.Conflict, "invalid_state");
            }

            if (job.State is "queued" or "running" or "validating")
            {
                job.CancelRequested = true;
            }
            else
            {
                Stop(job, "cancelled");
            }

            return Reply(HttpStatusCode.Accepted, job);
        }

        private HttpResponseMessage Resume(Job job)
        {
            if (job.State is "queued" or "running" or "analyzing")
            {
                return Reply(HttpStatusCode.Accepted, job);
            }

            if (job.State is not ("failed" or "cancelled"))
            {
                return Error(HttpStatusCode.Conflict, "invalid_state");
            }

            if (!job.Staged)
            {
                return Error(HttpStatusCode.Conflict, "source_required");
            }

            job.State = job.ConfirmedPreviewId is null ? "analyzing" : "queued";
            job.Error = null;
            job.CancelRequested = false;
            job.RetainedUntil = null;
            return Reply(HttpStatusCode.Accepted, job);
        }

        private async Task<HttpResponseMessage> AdvanceAsync(DatabaseScope scope, DatabaseRegion home, Job job, CancellationToken ct)
        {
            if (job.CancelRequested && job.State is "queued" or "running" or "validating")
            {
                Stop(job, "cancelled");
            }
            else if (job.State == "analyzing")
            {
                var source = job.Received.ToArray();
                job.Fixture = job.Format == "postgres_plain" && Encoding.UTF8.GetString(source).StartsWith("-- TerraScale import fixture: shop\n", StringComparison.Ordinal) ? "shop"
                    : job.Format == "sqlite" && source.Length >= 100 && Encoding.ASCII.GetString(source, 0, 16) == "SQLite format 3\0" ? source[18] == 1 && source[19] == 1 ? "notes" : "wal" : "";
                if (job.Format == "ndjson" && job.Collection is not null)
                {
                    job.PlainDocuments = Encoding.UTF8.GetString(source).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
                    job.Fixture = job.PlainDocuments.All(row => row.ValueKind == JsonValueKind.Object) ? "ndjson" : "";
                }
                if (job.Fixture is "" or "wal")
                {
                    job.State = "rejected";
                    job.Error = job.Fixture == "wal" ? Failure("wal_mode_source", "The SQLite file is in WAL mode; rows in its -wal file would be missing.", false)
                        : Failure("unsupported_source_format", "The file is not an accepted export.", false);
                }
                else
                {
                    job.PreviewId = Guid.CreateVersion7().ToString("N").ToUpperInvariant()[..26];
                    job.State = Preview(job)["blocking"]!.GetValue<int>() > 0 ? "rejected" : "ready";
                }
            }
            else if (job.State == "queued")
            {
                job.State = "running";
            }
            else if (job.State == "running")
            {
                if (job.Fail is { } fail)
                {
                    job.Fail = null;
                    Stop(job, "failed");
                    job.Error = Failure(fail["code"]!.GetValue<string>(), "Storage refused the batch.", fail["retryable"]!.GetValue<bool>(), Tables(job)[0].Source, job.Written + 1);
                    return Reply(HttpStatusCode.OK, job);
                }
                var total = Tables(job).Sum(table => table.Rows);
                var batch = Math.Min(2, total - job.Written);
                for (var row = job.Written; row < job.Written + batch; row++)
                {
                    var (table, index) = Locate(job, row);
                    await documents.CreateCollectionAsync(scope, home, job.Database, table.Collection, ct);
                    await documents.WriteAsync(scope, home, job.Database, table.Collection, "create", "01K78D0C00000000" + row.ToString("D10", CultureInfo.InvariantCulture),
                        job.Fixture == "ndjson" ? job.PlainDocuments[(int)index] : JsonSerializer.SerializeToElement(new { row = index + 1 }), "", ct);
                }
                job.Written += batch;
                if (job.Written == total)
                {
                    job.State = "validating";
                }
            }
            else if (job.State == "validating")
            {
                job.State = "completed";
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.Staged = false;
                job.Validation = new()
                {
                    ["passed"] = true,
                    ["tables"] = new JsonArray(Tables(job).Select(table => (JsonNode)new JsonObject
                    {
                        ["source"] = table.Source,
                        ["collection"] = table.Collection,
                        ["sourceRows"] = table.Rows,
                        ["documents"] = table.Rows,
                        ["sourceChecksum"] = new string('a', 64),
                        ["documentChecksum"] = new string('a', 64),
                        ["match"] = true
                    }).ToArray())
                };
            }
            return Reply(HttpStatusCode.OK, job);
        }

        private static void Stop(Job job, string state)
        {
            job.State = state;
            job.CancelRequested = false;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.RetainedUntil = job.Staged ? DateTimeOffset.UtcNow.AddDays(7) : null;
        }

        private static JsonObject Failure(string code, string message, bool retryable, string? table = null, long? row = null)
        {
            return new() { ["code"] = code, ["message"] = message, ["retryable"] = retryable, ["table"] = table, ["row"] = row, ["column"] = null };
        }

        private sealed record Column(string Name, string SourceType, string Mapping, bool Nullable = true);
        private sealed record Table(string Source, string Collection, long Rows, string[] PrimaryKey, Column[] Columns, JsonObject[] Constraints);

        private static readonly Table[] Shop =
        [
            new("public.customers", "customers", 3, ["id"], [new("id", "bigint", "integer", false), new("email", "citext", "string"), new("balance", "numeric(12,2)", "decimal_string"),
                new("created_at", "timestamptz", "timestamptz"), new("avatar", "bytea", "base64")],
                [Constraint("unique", "customers_email_key", ["email"], null)]),
            new("public.orders", "orders", 4, ["id"], [new("id", "uuid", "string", false), new("customer_id", "bigint", "integer"), new("total", "numeric", "decimal_string"),
                new("items", "jsonb", "json"), new("tags", "text[]", "unsupported")],
                [Constraint("foreign_key", "orders_customer_fk", ["customer_id"], "public.customers(id)")]),
            new("public.audit_log", "audit_log", 2, [], [new("payload", "jsonb", "json")], [])
        ];
        private static readonly Table[] Notes =
        [
            new("main.notes", "notes", 4, ["id"], [new("id", "INTEGER", "integer", false), new("body", "TEXT", "string"), new("attachment", "BLOB", "base64"), new("score", "NUMERIC", "integer+string")], []),
            new("main.tags", "tags", 3, ["note_id", "tag"], [new("note_id", "INTEGER", "integer", false), new("tag", "TEXT", "string", false)],
                [Constraint("primary_key", null, ["note_id", "tag"], null), Constraint("foreign_key", null, ["note_id"], "main.notes(id)")]),
            new("main.events", "events", 2, [], [new("kind", "TEXT", "string")], [])
        ];

        private static JsonObject Constraint(string kind, string? name, string[] columns, string? references)
        {
            return new() { ["kind"] = kind, ["name"] = name, ["columns"] = new JsonArray(columns.Select(item => (JsonNode?)item).ToArray()), ["references"] = references, ["enforced"] = false };
        }

        private static Table[] SourceTables(Job job)
        {
            return job.Fixture == "ndjson" ? [new("main." + job.Collection, job.Collection!, job.PlainDocuments.Length, [], [new("document", "jsonb", "json")], [])]
            : job.Fixture == "shop" ? Shop : Notes;
        }

        private static Table[] Tables(Job job)
        {
            return SourceTables(job).Where(table => !job.Exclude.Contains(table.Source)).ToArray();
        }

        private static (Table Table, long Index) Locate(Job job, long row)
        {
            foreach (var table in Tables(job)) { if (row < table.Rows) { return (table, row); } row -= table.Rows; }
            throw new InvalidOperationException("Row beyond the fixture.");
        }

        private static JsonObject Preview(Job job)
        {
            var tables = new JsonArray();
            int blocking = 0, warnings = 0;
            foreach (var table in SourceTables(job))
            {
                var excluded = job.Exclude.Contains(table.Source);
                var issues = new JsonArray();
                var columns = new JsonArray();
                foreach (var column in table.Columns)
                {
                    var columnExcluded = job.Exclude.Contains(table.Source + "." + column.Name);
                    columns.Add(new JsonObject { ["name"] = column.Name, ["sourceType"] = column.SourceType, ["mapping"] = column.Mapping, ["nullable"] = column.Nullable, ["excluded"] = columnExcluded });
                    if (excluded || columnExcluded)
                    {
                        continue;
                    }

                    if (column.Mapping == "unsupported")
                    {
                        issues.Add(Issue("blocking", "unsupported_type", column.Name, column.SourceType + " has no mapping; exclude the column"));
                    }

                    if (column.Mapping.Contains('+'))
                    {
                        issues.Add(Issue("warning", "mixed_storage_classes", column.Name, "Values use more than one storage class."));
                    }
                }
                blocking += issues.Count(issue => issue!["severity"]!.GetValue<string>() == "blocking");
                warnings += issues.Count(issue => issue!["severity"]!.GetValue<string>() == "warning");
                tables.Add(new JsonObject
                {
                    ["source"] = table.Source,
                    ["collection"] = table.Collection,
                    ["excluded"] = excluded,
                    ["rows"] = table.Rows,
                    ["primaryKey"] = new JsonArray(table.PrimaryKey.Select(item => (JsonNode?)item).ToArray()),
                    ["columns"] = columns,
                    ["constraints"] = new JsonArray(table.Constraints.Select(item => (JsonNode?)item.DeepClone()).ToArray()),
                    ["issues"] = issues
                });
            }
            var skipped = job.Fixture == "shop"
                ? new[] { ("function", "public.touch()"), ("trigger", "orders_touch"), ("check_constraint", "orders_total_check"), ("sequence", "public.customers_id_seq") }
                : new[] { ("virtual_table", "main.notes_fts"), ("shadow_table", "main.notes_fts_data"), ("internal_table", "main.sqlite_sequence") };
            return new()
            {
                ["id"] = job.PreviewId,
                ["tables"] = tables,
                ["blocking"] = blocking,
                ["warnings"] = warnings,
                ["skippedTruncated"] = false,
                ["skipped"] = new JsonArray(skipped.Select(item => (JsonNode?)new JsonObject { ["kind"] = item.Item1, ["name"] = item.Item2 }).ToArray())
            };
        }

        private static JsonObject Issue(string severity, string code, string? column, string message)
        {
            return new() { ["severity"] = severity, ["code"] = code, ["column"] = column, ["row"] = null, ["message"] = message };
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, Job job)
        {
            var total = job.Fixture.Length == 0 || job.Fixture == "wal" ? 0 : Tables(job).Sum(table => table.Rows);
            var body = new JsonObject
            {
                ["id"] = job.Id,
                ["project"] = job.Project,
                ["database"] = job.Database,
                ["region"] = "gru",
                ["state"] = job.State,
                ["cancelRequested"] = job.CancelRequested,
                ["options"] = new JsonObject { ["exclude"] = new JsonArray(job.Exclude.Select(item => (JsonNode?)item).ToArray()), ["collection"] = job.Collection },
                ["source"] = new JsonObject
                {
                    ["format"] = job.Format,
                    ["bytes"] = job.Bytes,
                    ["sha256"] = job.Sha256,
                    ["receivedBytes"] = job.Received.Length,
                    ["staged"] = job.Staged,
                    ["retainedUntil"] = job.RetainedUntil?.ToString("O")
                },
                ["preview"] = job.PreviewId is null ? null : Preview(job),
                ["confirmedPreviewId"] = job.ConfirmedPreviewId,
                ["progress"] = job.StartedAt is null ? null : new JsonObject
                {
                    ["tablesDone"] = Tables(job).Count(table => Tables(job).TakeWhile(item => item != table).Sum(item => item.Rows) + table.Rows <= job.Written),
                    ["tablesTotal"] = Tables(job).Length,
                    ["rowsWritten"] = job.Written,
                    ["rowsTotal"] = total,
                    ["currentTable"] = job.State == "running" && job.Written < total ? Locate(job, job.Written).Table.Source : null
                },
                ["validation"] = job.Validation?.DeepClone(),
                ["error"] = job.Error?.DeepClone(),
                ["createdAt"] = job.CreatedAt.ToString("O"),
                ["updatedAt"] = job.UpdatedAt.ToString("O"),
                ["startedAt"] = job.StartedAt?.ToString("O"),
                ["finishedAt"] = job.FinishedAt?.ToString("O")
            };
            return new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = code } }.ToJsonString(), Encoding.UTF8, "application/json") };
            if (status == HttpStatusCode.MisdirectedRequest)
            {
                response.Headers.Add("X-TerraBase-Database-Home", "gru");
            }

            return response;
        }
    }
}
