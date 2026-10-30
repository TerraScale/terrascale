using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Home;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.E2E
{
    public sealed record ReferencePersonas(ReferencePrincipal Routes, ReferencePrincipal Seeded);

    // These are the actual identities and illustrative records used by the Go
    // exporter, not replacements for pages or snapshots. Only this test binary
    // receives them; native sign-in and the production renderers remain in use.
    internal static class ReferenceFixtures
    {
        internal const string MarioSubject = "7CVZQJY80VY06TJG7VP8TQ44DM";
        internal const string LabsId = "7N1PZ3BD9WCGBVV4V50V508NRX";
        internal const string CoreId = "1X1TETYPPH2APBVN5WGY9D3S3G";
        internal const string EdgeId = "1411Y0F25R3D1RAZWB7628Q3JH";
        internal const string SandboxId = "06RCAG0WTSX0132AWJ5NEK4KR8";
        internal const string PrimaryId = "0Z793APJ2ECFW2F7P6J9F2RKVY";

        public static async Task<ReferencePersonas> InitializeAsync(string root, TestState state, TestWorkspace workspace,
            Action<ReferencePrincipal> registerPrincipal, Action<ManagedDatabase> registerDatabase, TestDocuments documents)
        {
            using var manifest = await ReadManifestAsync(root);
            var routes = ReadPersona(manifest.RootElement.GetProperty("routes"));
            var seeded = ReadPersona(manifest.RootElement.GetProperty("seeded"));
            if (seeded.Principal.Subject != MarioSubject || routes.Principal.Subject == seeded.Principal.Subject ||
                routes.OrganizationIds.Count != 1 || seeded.OrganizationIds.Count != 2 ||
                seeded.ActiveOrganizationId != LabsId || seeded.ActiveProjectId != CoreId)
            {
                throw new InvalidDataException("The source manifest no longer describes the captured Go reference personas.");
            }

            var personas = new ReferencePersonas(routes.Principal, seeded.Principal);
            registerPrincipal(personas.Routes);
            registerPrincipal(personas.Seeded);

            var personal = routes.Personal;
            ApplyCapturedMembers(personal, routes.Members[personal.Id]);
            ApplyCapturedInvitations(personal, routes.Invitations[personal.Id]);
            await PersistOrganizationAsync(state, workspace, personal);
            await PersistAccountAsync(state, personas.Routes, routes.OrganizationIds, routes.ActiveOrganizationId, routes.ActiveProjectId);

            var marioPersonal = seeded.Personal;
            ApplyCapturedMembers(marioPersonal, seeded.Members[marioPersonal.Id]);
            ApplyCapturedInvitations(marioPersonal, seeded.Invitations[marioPersonal.Id]);
            await PersistOrganizationAsync(state, workspace, marioPersonal);
            var labs = Labs();
            ApplyOrganizationMetadata(labs, seeded.Organizations.Single(item => item.GetProperty("id").GetString() == LabsId));
            ApplyCapturedMembers(labs, seeded.Members[labs.Id]);
            ApplyCapturedInvitations(labs, seeded.Invitations[labs.Id]);
            await PersistOrganizationAsync(state, workspace, labs);
            await PersistAccountAsync(state, personas.Seeded, seeded.OrganizationIds, seeded.ActiveOrganizationId, seeded.ActiveProjectId, Notifications());
            await state.PutAsync(StripeBilling.Collection, LabsId, JsonSerializer.SerializeToElement(new BillingBinding
            {
                OrganizationId = LabsId,
                CustomerId = ReferenceStripeTransport.CustomerId,
                RequestId = Guid.NewGuid().ToString("N"),
                Plan = "pro",
                Interval = "monthly",
                EffectivePlan = "pro",
                EffectiveInterval = "monthly",
                Status = "active",
                PaymentValidated = true,
                ReturnPath = "/organization?tab=billing",
                StartedAt = At(2026, 1, 12, 9, 24)
            }), TerraBasePrecondition.Absent, default);
            await state.PutAsync(StripeBilling.CustomersCollection, ReferenceStripeTransport.CustomerId,
                JsonSerializer.SerializeToElement(new CustomerBinding(LabsId)), TerraBasePrecondition.Absent, default);

            foreach (var project in labs.Projects.Values)
            {
                foreach (var database in project.Databases.Values)
                {
                    registerDatabase(new(database.Id, database.ProjectId, database.Name, database.CreatedAt, HomeRegion: database.Region));
                }
            }

            await SeedDocumentsAsync(documents, CoreId, PrimaryId, "core-primary", "SJC", "orders",
            [
                ("01K78000000000000000001001", "{\"customerId\":\"cus_1001\",\"createdAt\":\"2026-09-17T07:02:00Z\",\"status\":\"paid\",\"total\":725.5}"),
                ("01K78000000000000000001002", "{\"customerId\":\"cus_1002\",\"createdAt\":\"2026-09-16T12:15:00Z\",\"status\":\"paid\",\"total\":980.0}"),
                ("01K78000000000000000001003", "{\"customerId\":\"cus_1003\",\"createdAt\":\"2026-09-15T15:44:00Z\",\"status\":\"open\",\"total\":140.0}")
            ]);
            await SeedDocumentsAsync(documents, CoreId, "3S7V1R9W8CKJM87P94PBACW79D", "core-sessions", "SJC", "sessions",
            [
                ("488YCSAW1HEH6XFB1KYKT1YQB4", "{\"userId\":\"2RXDD6A70VYXH8M0SDAKFWAQT1\",\"startedAt\":\"2026-09-17T10:24:00Z\",\"status\":\"active\"}"),
                ("7KM4NDWC9RDSEHJQ64DRBNMK69", "{\"userId\":\"5KPC8W3N5TA88HVG4S068X0MD7\",\"startedAt\":\"2026-09-12T08:10:00Z\",\"status\":\"expired\"}")
            ]);
            await SeedDocumentsAsync(documents, EdgeId, "185JTBEGC768240CCVHG6TGQH4", "edge-events", "GRU", "events",
            [
                ("12K95R3TTQBAV0C4VSQ4H3H1D1", "{\"type\":\"checkout.completed\",\"occurredAt\":\"2026-09-17T14:10:00Z\"}"),
                ("133XABR9K9YRWKZ26JVK0K7R3R", "{\"type\":\"session.started\",\"occurredAt\":\"2026-09-17T14:12:00Z\"}")
            ]);
            var saved = new SavedQueryRecord(LabsId, CoreId,
            [
                new("39ZEVV1QSZ378459T0K4B2R2WJ", "orders-over-500", "SELECT * FROM orders WHERE total > 500", "", At(2026, 9, 16, 10, 20)),
                new("01RDCAGXABDJ1KBJ6RGJZM0FE3", "sessions-active-7d", "SELECT id, startedAt FROM sessions WHERE startedAt > now() - 7d", "", At(2026, 9, 14, 16, 44))
            ]);
            await state.PutAsync("dashboard_saved_queries", Hash(LabsId + "/" + CoreId), JsonSerializer.SerializeToElement(saved), TerraBasePrecondition.Absent, default);
            return personas;
        }

        // Captured once from the retired Go application (source.commit records the revision).
        internal static string ManifestPath(string root)
        {
            return Path.Combine(root, "tests", "TerraScale.Dashboard.E2E", "Fixtures", "reference-workspace.json");
        }

        internal static async Task<JsonDocument> ReadManifestAsync(string root)
        {
            // Keep the archived Go capture intact; bind its obsolete region values to today's demo catalog
            // when constructing test inputs and expectations. This never changes production routing.
            var source = await File.ReadAllTextAsync(ManifestPath(root));
            var manifest = JsonDocument.Parse(source
                .Replace("\"SFO\"", "\"SJC\"", StringComparison.Ordinal)
                .Replace("\"SFO \\u00B7 San Francisco\"", "\"SJC\"", StringComparison.Ordinal));
            if (manifest.RootElement.GetProperty("version").GetInt32() == 1)
            {
                return manifest;
            }

            manifest.Dispose();
            throw new InvalidDataException("Unsupported reference workspace fixture version.");
        }

        private static PersonaFixture ReadPersona(JsonElement value)
        {
            var identity = value.GetProperty("identity");
            var principal = new ReferencePrincipal(TerraScaleIds.Validate(identity.GetProperty("userId").GetString()),
                identity.GetProperty("name").GetString()!, identity.GetProperty("email").GetString()!, value.GetProperty("password").GetString()!,
                TerraScaleIds.Validate(identity.GetProperty("accountId").GetString()), identity.GetProperty("phone").GetString()!, identity.GetProperty("locale").GetString()!);
            var context = value.GetProperty("context");
            var user = context.GetProperty("user");
            var selection = context.GetProperty("selection");
            var organizationId = TerraScaleIds.Validate(selection.GetProperty("organizationId").GetString());
            var projectId = TerraScaleIds.Validate(selection.GetProperty("projectId").GetString());
            if (user.GetProperty("id").GetString() != principal.Subject || user.GetProperty("name").GetString() != principal.Name ||
                user.GetProperty("email").GetString() != principal.Email || identity.GetProperty("activeOrganizationId").GetString() != organizationId ||
                identity.GetProperty("activeProjectId").GetString() != projectId || principal.Password.Length == 0)
            {
                throw new InvalidDataException("The source profile and context identities or selection disagree.");
            }

            var summaries = context.GetProperty("organizations").EnumerateArray().ToArray();
            var organizations = value.GetProperty("organizations").EnumerateArray().Select(item => item.Clone()).ToArray();
            var ids = summaries.Select(item => TerraScaleIds.Validate(item.GetProperty("id").GetString())).ToList();
            if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count() || organizations.Length != ids.Count ||
                !organizations.Select(item => item.GetProperty("id").GetString()).Order(StringComparer.Ordinal).SequenceEqual(ids.Order(StringComparer.Ordinal)))
            {
                throw new InvalidDataException("The source context and organization inventories disagree.");
            }

            foreach (var summary in summaries)
            {
                var detail = organizations.Single(item => item.GetProperty("id").GetString() == summary.GetProperty("id").GetString());
                foreach (var field in new[] { "name", "kind", "status", "role", "planId", "billingStatus" })
                {
                    if (summary.GetProperty(field).GetString() != detail.GetProperty(field).GetString())
                    {
                        throw new InvalidDataException("The source organization summary and detail disagree.");
                    }
                }

                var projects = summary.GetProperty("projects").EnumerateArray().ToArray();
                var details = detail.GetProperty("projects").EnumerateArray().ToArray();
                if (projects.Length != details.Length)
                {
                    throw new InvalidDataException("The source organization project inventories disagree.");
                }

                foreach (var project in projects)
                {
                    var id = TerraScaleIds.Validate(project.GetProperty("id").GetString());
                    var projectDetail = details.Single(item => item.GetProperty("id").GetString() == id);
                    foreach (var field in new[] { "name", "region", "backupRegion" })
                    {
                        if (project.GetProperty(field).GetString() != projectDetail.GetProperty(field).GetString())
                        {
                            throw new InvalidDataException("The source project summary and detail disagree.");
                        }
                    }

                    if (project.GetProperty("isDefault").GetBoolean() != projectDetail.GetProperty("isDefault").GetBoolean() ||
                        project.GetProperty("databases").GetInt32() != projectDetail.GetProperty("databases").GetInt32() ||
                        !project.GetProperty("regions").EnumerateArray().Select(item => item.GetString()).SequenceEqual(
                            projectDetail.GetProperty("regions").EnumerateArray().Select(item => item.GetString())))
                    {
                        throw new InvalidDataException("The source project placement or database counts disagree.");
                    }
                }
            }
            var selected = summaries.Single(item => item.GetProperty("id").GetString() == organizationId);
            if (!selected.GetProperty("projects").EnumerateArray().Any(item => item.GetProperty("id").GetString() == projectId))
            {
                throw new InvalidDataException("The source selection does not belong to the captured project scope.");
            }
            // These captured Go public User IDs are explicitly enrolled as test
            // producer subjects. Real import still requires User.ID -> AuthSubject
            // mapping; neither platformAdmin nor demo MFA records become claims.
            var personal = Personal(principal, organizations.Single(item => item.GetProperty("kind").GetString() == "personal"));
            return new(principal, personal, organizationId, projectId, ids, organizations,
                ReadCapturedMembers(value, principal, organizations), ReadCapturedInvitations(value, organizations));
        }

        private sealed record PersonaFixture(ReferencePrincipal Principal, OrganizationRecord Personal, string ActiveOrganizationId,
            string ActiveProjectId, List<string> OrganizationIds, IReadOnlyList<JsonElement> Organizations,
            IReadOnlyDictionary<string, IReadOnlyList<CapturedMember>> Members,
            IReadOnlyDictionary<string, IReadOnlyList<CapturedInvitation>> Invitations);
        private sealed record CapturedMember(string Id, string Name, string Email, string Role, DateOnly JoinedOn);
        private sealed record CapturedInvitation(string Id, string Email, string Role, string Status, DateTimeOffset ExpiresAt);

        private static IReadOnlyDictionary<string, IReadOnlyList<CapturedInvitation>> ReadCapturedInvitations(JsonElement persona,
            IReadOnlyList<JsonElement> organizations)
        {
            if (!persona.TryGetProperty("invitations", out var inventory) || inventory.ValueKind != JsonValueKind.Array ||
                inventory.GetArrayLength() != organizations.Count)
            {
                throw new InvalidDataException("Capture the actual source invitation inventory for every organization before comparing applications.");
            }

            var result = new Dictionary<string, IReadOnlyList<CapturedInvitation>>(StringComparer.Ordinal);
            foreach (var entry in inventory.EnumerateArray())
            {
                var organizationId = TerraScaleIds.Validate(entry.GetProperty("organizationId").GetString());
                if (!organizations.Any(org => org.GetProperty("id").GetString() == organizationId) || result.ContainsKey(organizationId))
                {
                    throw new InvalidDataException("The source invitation scope is duplicated or absent from the source context.");
                }

                var invitations = new List<CapturedInvitation>();
                var rows = entry.GetProperty("result").GetProperty("invitations");
                if (rows.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
                {
                    throw new InvalidDataException("The source invitation response is not a nullable array.");
                }

                foreach (var row in rows.ValueKind == JsonValueKind.Null ? [] : rows.EnumerateArray().ToArray())
                {
                    var id = TerraScaleIds.Validate(row.GetProperty("id").GetString());
                    var status = row.GetProperty("status").GetString()!;
                    var open = row.GetProperty("open").GetBoolean();
                    var expiry = row.GetProperty("expiresAt").GetString();
                    var role = row.GetProperty("role").GetString()!;
                    var expiresAt = DateTimeOffset.MinValue;
                    if (status is not ("Pending" or "Accepted" or "Declined" or "Canceled" or "Expired") ||
                        role is not ("owner" or "admin" or "member" or "viewer") || invitations.Any(invitation => invitation.Id == id) ||
                        open && status is not ("Pending" or "Expired") || !open && status == "Pending" ||
                        expiry != "" && !DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresAt) ||
                        open && status == "Expired" && expiresAt == DateTimeOffset.MinValue)
                    {
                        throw new InvalidDataException("A captured invitation has invalid identity, role, status, open state or expiry precision.");
                    }
                    // The source read exposes no secret or created timestamp.
                    // An empty expiry remains unreported, never a fabricated grant.
                    invitations.Add(new(id, row.GetProperty("email").GetString()!, role,
                        open ? "pending" : status.ToLowerInvariant(), expiresAt));
                }
                result.Add(organizationId, invitations);
            }
            return result;
        }

        private static void ApplyCapturedInvitations(OrganizationRecord organization, IReadOnlyList<CapturedInvitation> captured)
        {
            organization.Invitations = captured.ToDictionary(invitation => invitation.Id, invitation => new InvitationRecord
            {
                Id = invitation.Id,
                OrganizationId = organization.Id,
                Email = invitation.Email,
                Role = invitation.Role,
                Status = invitation.Status,
                ExpiresAt = invitation.ExpiresAt
            }, StringComparer.Ordinal);
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<CapturedMember>> ReadCapturedMembers(JsonElement persona,
            ReferencePrincipal principal, IReadOnlyList<JsonElement> organizations)
        {
            if (!persona.TryGetProperty("members", out var inventory) || inventory.ValueKind != JsonValueKind.Array ||
                inventory.GetArrayLength() != organizations.Count)
            {
                throw new InvalidDataException("The source manifest must contain the actual authenticated membership inventory for every organization.");
            }

            var result = new Dictionary<string, IReadOnlyList<CapturedMember>>(StringComparer.Ordinal);
            foreach (var entry in inventory.EnumerateArray())
            {
                var id = TerraScaleIds.Validate(entry.GetProperty("organizationId").GetString());
                var organization = organizations.SingleOrDefault(org => org.GetProperty("id").GetString() == id);
                if (organization.ValueKind != JsonValueKind.Object || result.ContainsKey(id))
                {
                    throw new InvalidDataException("The source membership organization inventory is duplicated or disagrees with the captured context.");
                }

                var response = entry.GetProperty("result");
                var rows = response.GetProperty("members");
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != organization.GetProperty("memberCount").GetInt32())
                {
                    throw new InvalidDataException("The source membership count disagrees with its captured organization.");
                }

                var members = new List<CapturedMember>();
                foreach (var row in rows.EnumerateArray())
                {
                    var userId = TerraScaleIds.Validate(row.GetProperty("id").GetString());
                    var role = row.GetProperty("role").GetString()!;
                    var joined = row.GetProperty("joinedAt").GetString()!;
                    if (role is not ("owner" or "admin" or "member" or "viewer") ||
                        !DateOnly.TryParseExact(joined, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != joined ||
                        row.GetProperty("isSelf").GetBoolean() != (userId == principal.Subject) ||
                        row.GetProperty("isOwner").GetBoolean() != (role == "owner") || members.Any(member => member.Id == userId))
                    {
                        throw new InvalidDataException("A source member identity, role, owner/self binding or exact join date is invalid.");
                    }

                    members.Add(new(userId, row.GetProperty("name").GetString()!, row.GetProperty("email").GetString()!, role, date));
                }
                var self = members.SingleOrDefault(member => member.Id == principal.Subject);
                if (self is null || self.Name != principal.Name || self.Email != principal.Email || self.Role != organization.GetProperty("role").GetString())
                {
                    throw new InvalidDataException("The captured member does not bind to the source principal and organization role.");
                }

                var counts = response.GetProperty("roleCounts");
                foreach (var role in new[] { "owner", "member", "viewer" })
                {
                    if (counts.GetProperty(role).GetInt32() != members.Count(member => member.Role == role))
                    {
                        throw new InvalidDataException("The source membership role counts disagree with their actual rows.");
                    }
                }

                result.Add(id, members);
            }
            return result;
        }

        private static void ApplyCapturedMembers(OrganizationRecord organization, IReadOnlyList<CapturedMember> captured)
        {
            var members = new Dictionary<string, MemberRecord>(StringComparer.Ordinal);
            foreach (var member in captured)
            {
                // list_members reports date precision only. Keep a timestamp only
                // where the actual Go fixture source supplied one; never invent
                // midnight or borrow the organization's creation timestamp.
                var known = organization.Members.GetValueOrDefault(member.Id)?.CreatedAt ?? default;
                if (known != DateTimeOffset.MinValue && DateOnly.FromDateTime(known.UtcDateTime) != member.JoinedOn)
                {
                    throw new InvalidDataException("The source member join date contradicts its known fixture timestamp.");
                }

                members.Add(member.Id, new MemberRecord
                {
                    UserId = member.Id,
                    Name = member.Name,
                    Email = member.Email,
                    Role = member.Role,
                    CreatedAt = known,
                    JoinedOn = member.JoinedOn
                });
            }
            organization.Members = members;
        }

        private static async Task PersistAccountAsync(TestState state, ReferencePrincipal principal, List<string> organizations,
            string activeOrganization, string activeProject, List<NotificationRecord>? notifications = null)
        {
            // Import the public source ID while preserving the private subject
            // digest and its canonical, create-only ownership index.
            var storageKey = AccountService.AccountStorageKey(principal.Subject);
            var account = new AccountRecord
            {
                Id = principal.SourceAccountId,
                UserId = principal.Subject,
                Name = principal.Name,
                Email = principal.Email,
                Phone = principal.Phone,
                Locale = principal.Locale,
                OnboardingCompleted = true,
                OnboardingStep = "complete",
                OrganizationIds = organizations,
                ActiveOrganizationId = activeOrganization,
                ActiveProjectId = activeProject,
                Notifications = notifications ?? []
            };
            await state.PutAsync(AccountService.IdentitiesCollection, storageKey, JsonSerializer.SerializeToElement(new AccountIdentityBinding(principal.Subject, account.Id)), TerraBasePrecondition.Absent, default);
            await state.PutAsync(AccountService.AccountsCollection, storageKey, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Absent, default);
        }

        private static async Task PersistOrganizationAsync(TestState state, TestWorkspace workspace, OrganizationRecord organization)
        {
            lock (workspace.Gate)
            {
                if (workspace.Organizations.ContainsKey(organization.Id))
                {
                    throw new InvalidDataException("A Go reference organization is already registered.");
                }

                workspace.Organizations[organization.Id] = TestWorkspace.Clone(organization);
                workspace.SeedInvitationMetadata(organization);
            }
            await state.PutAsync(AccountService.OrganizationsCollection, organization.Id, JsonSerializer.SerializeToElement(organization), TerraBasePrecondition.Absent, default);
        }

        private static OrganizationRecord Personal(ReferencePrincipal principal, JsonElement detail)
        {
            if (detail.GetProperty("kind").GetString() != "personal" || detail.GetProperty("role").GetString() != "owner" ||
                detail.GetProperty("memberCount").GetInt32() != 1 || detail.GetProperty("projects").GetArrayLength() != 1)
            {
                throw new InvalidDataException("The source Personal workspace fixture no longer matches the captured owner.");
            }

            var organization = new OrganizationRecord { Id = TerraScaleIds.Validate(detail.GetProperty("id").GetString()), OwnerUserId = principal.Subject };
            organization.Members[principal.Subject] = new() { UserId = principal.Subject, Name = principal.Name, Email = principal.Email, Role = "owner" };
            foreach (var project in detail.GetProperty("projects").EnumerateArray())
            {
                var id = TerraScaleIds.Validate(project.GetProperty("id").GetString());
                if (project.GetProperty("databases").GetInt32() != 0)
                {
                    throw new InvalidDataException("The captured Personal project is no longer empty.");
                }
                // The source DTO reports neither project/member/account CreatedAt
                // nor project slug. Keep them unreported rather than infer them
                // from the organization timestamp or manifest collection time.
                organization.Projects[id] = new() { Id = id, OrganizationId = organization.Id };
            }
            ApplyOrganizationMetadata(organization, detail);
            return organization;
        }

        private static void ApplyOrganizationMetadata(OrganizationRecord organization, JsonElement detail)
        {
            if (detail.GetProperty("id").GetString() != organization.Id)
            {
                throw new InvalidDataException("The source organization identity disagrees with its fixture projection.");
            }

            organization.Name = detail.GetProperty("name").GetString()!;
            organization.Kind = detail.GetProperty("kind").GetString()!;
            organization.Slug = detail.GetProperty("slug").GetString()!;
            organization.Status = detail.GetProperty("status").GetString()!;
            organization.PlanId = TerraScaleIds.Validate(detail.GetProperty("planId").GetString());
            organization.BillingStatus = detail.GetProperty("billingStatus").GetString()!;
            organization.DeletionRequested = detail.GetProperty("deletionRequested").GetBoolean();
            organization.CreatedAt = detail.GetProperty("createdAt").GetDateTimeOffset();
            var projects = detail.GetProperty("projects").EnumerateArray().ToArray();
            if (projects.Length != organization.Projects.Count)
            {
                throw new InvalidDataException("The source project count disagrees with its fixture projection.");
            }

            foreach (var summary in projects)
            {
                var id = TerraScaleIds.Validate(summary.GetProperty("id").GetString());
                var project = organization.Projects.GetValueOrDefault(id) ?? throw new InvalidDataException("A captured source project is missing from its fixture projection.");
                if (summary.GetProperty("databases").GetInt32() != project.Databases.Count)
                {
                    throw new InvalidDataException("The captured source database inventory has changed.");
                }

                project.Name = summary.GetProperty("name").GetString()!;
                project.Region = summary.GetProperty("region").GetString()!;
                project.BackupRegion = summary.GetProperty("backupRegion").GetString()!;
                project.Regions = summary.GetProperty("regions").EnumerateArray().Select(item => item.GetString()!).ToList();
                project.IsDefault = summary.GetProperty("isDefault").GetBoolean();
            }
        }

        private static OrganizationRecord Labs()
        {
            var core = Project(CoreId, "core-platform", "SJC", true, At(2026, 1, 12, 9, 24));
            var edge = Project(EdgeId, "edge-analytics", "GRU", false, At(2026, 2, 3, 14, 2));
            var sandbox = Project(SandboxId, "staging-sandbox", "GRU", false, At(2026, 3, 21, 11, 47));
            core.Databases[PrimaryId] = Database(PrimaryId, CoreId, "core-primary", "SJC", "ready", At(2026, 1, 12, 9, 31));
            core.Databases["3S7V1R9W8CKJM87P94PBACW79D"] = Database("3S7V1R9W8CKJM87P94PBACW79D", CoreId, "core-sessions", "SJC", "ready", At(2026, 1, 14, 10, 2));
            edge.Databases["185JTBEGC768240CCVHG6TGQH4"] = Database("185JTBEGC768240CCVHG6TGQH4", EdgeId, "edge-events", "GRU", "ready", At(2026, 2, 3, 14, 11));
            sandbox.Databases["0YF85EHHPE3AVMCTS72XX4RVYA"] = Database("0YF85EHHPE3AVMCTS72XX4RVYA", SandboxId, "sandbox", "GRU", "provisioning", At(2026, 9, 17, 8, 15));
            return new()
            {
                Id = LabsId,
                Name = "TerraScale Labs",
                Kind = "business",
                Slug = "terrascale-labs",
                OwnerUserId = MarioSubject,
                PlanId = "7CNW2H87JE68ATZX224CV2G625",
                BillingStatus = "active",
                PaymentMethodValidated = true,
                Projects = new() { [CoreId] = core, [EdgeId] = edge, [SandboxId] = sandbox },
                Members = new()
                {
                    [MarioSubject] = new() { UserId = MarioSubject, Name = "Mario Kraus", Email = "mario@terrascale.tech", Role = "owner", CreatedAt = At(2026, 9, 18, 7, 12) },
                    ["09MMHD518NFARFD5HB841BS8EA"] = new() { UserId = "09MMHD518NFARFD5HB841BS8EA", Name = "Ana Souza", Email = "ana@terrascale.tech", Role = "admin", CreatedAt = At(2026, 9, 17, 18, 40) },
                    ["2Q9DGMCYH1PHHNXHC03FEZNSME"] = new() { UserId = "2Q9DGMCYH1PHHNXHC03FEZNSME", Name = "Ravi Patel", Email = "ravi@terrascale.tech", Role = "member", CreatedAt = At(2026, 9, 15, 9, 3) }
                },
                AuditEvents =
                [
                    new("0S3WH091P8NPAFZ2Y5HVHTE8JQ", "mario@terrascale.tech", "api_key.created", "ci-deploy", "api_key", At(2026, 9, 18, 7, 5), "dashboard", "low"),
                    new("611MYHE1QDJXYERR85EY869Z5B", "ana@terrascale.tech", "member.role_changed", "ravi@terrascale.tech", "member", At(2026, 9, 17, 14, 22), "dashboard", "medium"),
                    new("5XXTNQMZNMDGT8DZ986C7XZYEC", "ci-deploy (api key)", "document.batch_upsert", "core-primary/orders", "database", At(2026, 9, 17, 6, 41), "api", "low"),
                    new("7T7392ARW2ZDPAX02X93X92S81", "mario@terrascale.tech", "database.deleted", "old-reporting", "database", At(2026, 9, 12, 11, 4), "dashboard", "high"),
                    new("0AMW61HE983N01PBS2240P39Q8", "system", "backup.restore_completed", "core-sessions", "database", At(2026, 9, 10, 2, 48), "scheduled", "medium")
                ]
            };
        }

        private static ProjectRecord Project(string id, string name, string region, bool isDefault, DateTimeOffset createdAt)
        {
            return new()
            { Id = id, OrganizationId = LabsId, Name = name, Slug = name, Region = region, Regions = [region], IsDefault = isDefault, CreatedAt = createdAt };
        }

        private static DatabaseRecord Database(string id, string project, string name, string region, string status, DateTimeOffset createdAt)
        {
            return new()
            { Id = id, ProjectId = project, Name = name, Region = region, Regions = [region], Status = status, CreatedAt = createdAt };
        }

        private static List<NotificationRecord> Notifications()
        {
            return [
            Notification("68946KQMGJWXNF2WTSWP370GJ6", CoreId, "api-key-expiry", "API key expiring soon", "ci-deploy expires in 7 days. Rotate it to avoid breaking the pipeline.", "unread", "warning", "/api-keys", At(2026, 9, 17, 9, 0)),
            Notification("258GKYJS87W1EJH7BPDK6C3NNF", "", "invitation", "Invitation pending", "jules@contractor.dev has not accepted the invitation yet.", "unread", "info", "/organization?tab=invitations", At(2026, 9, 16, 15, 5)),
            Notification("3M2P9VGAHSCQ1YVCCKYECN54DS", CoreId, "quota", "Storage at 82%", "core-primary is using 82% of the plan storage quota.", "read", "warning", "/usage", At(2026, 9, 14, 20, 30)),
            Notification("71W1N22DQNY5B9QSBVA965QT67", CoreId, "backup", "Backup completed", "core-primary nightly backup finished in 42s.", "read", "success", "/databases/" + PrimaryId + "/backups", At(2026, 9, 14, 3, 12))
        ];
        }

        private static NotificationRecord Notification(string id, string project, string type, string title, string body, string status, string severity, string url, DateTimeOffset createdAt)
        {
            return new()
            { Id = id, OrganizationId = LabsId, ProjectId = project, Type = type, Title = title, Body = body, Status = status, Severity = severity, Url = url, CreatedAt = createdAt };
        }

        private static async Task SeedDocumentsAsync(TestDocuments documents, string project, string databaseId, string databaseName, string region, string collection, (string Id, string Json)[] rows)
        {
            var scope = new DatabaseScope(MarioSubject, "", LabsId, "TerraScale Labs", project, project == CoreId ? "core-platform" : "edge-analytics");
            var home = new DatabaseRegion(region, region.ToLowerInvariant() + "-1", region == "SJC" ? "San Francisco Bay Area" : "São Paulo", region == "SJC" ? "us" : "br", new("https://" + region.ToLowerInvariant() + ".api.terrascale.tech"), true);
            await documents.CreateCollectionAsync(scope, home, databaseName, collection, default);
            foreach (var (id, json) in rows)
            {
                using var document = JsonDocument.Parse(json);
                await documents.WriteAsync(scope, home, databaseName, collection, "create", id, document.RootElement, "", default);
            }
        }

        private static string Hash(string value)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
        {
            return new(year, month, day, hour, minute, 0, TimeSpan.Zero);
        }
    }

    // Match demovisuals/workload.go's explicitly seeded illustrative series. None
    // of these sample values are presented as measured production usage.
    internal sealed class ReferenceMeasurements : IHomeMeasurements
    {
        public Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken)
        {
            var result = new HomeMeasurement
            {
                ActiveCredentials = 0,
                AnalyticsError = "",
                ReplicationError = "",
                AnalyticsEmpty = "Analytics measurements are unavailable.",
                ReplicationEmpty = "Replication measurements are unavailable."
            };
            if (context.UserId != ReferenceFixtures.MarioSubject || context.Organization?.Id != ReferenceFixtures.LabsId)
            {
                return Task.FromResult(result);
            }

            var count = range switch { "1h" => 12, "24h" => 24, "30d" => 30, _ => 168 };
            var baseline = range switch { "1h" or "24h" => 950d, "30d" => 820d, _ => 880d };
            var variance = range switch { "1h" or "24h" => 180d, "30d" => 420d, _ => 320d };
            var span = range switch { "1h" => TimeSpan.FromHours(1), "24h" => TimeSpan.FromHours(24), "30d" => TimeSpan.FromDays(30), _ => TimeSpan.FromDays(7) };
            var random = new Random(42);
            var points = new List<ActivityPoint>();
            var labels = new List<string>();
            var now = DateTimeOffset.UtcNow;
            var current = baseline;
            for (var index = 0; index < count; index++)
            {
                current = Math.Max(50, current + (random.NextDouble() - 0.48) * variance * 0.4);
                var operations = (long)Math.Round(current, MidpointRounding.AwayFromZero);
                var reads = (long)Math.Round(operations * (0.62 + 0.08 * Math.Sin(index * 0.35)), MidpointRounding.AwayFromZero);
                var age = TimeSpan.FromTicks(span.Ticks * (count - 1 - index) / count);
                points.Add(new(now - age, reads, operations - reads));
                labels.Add(Ago(age));
            }
            result.Activity = points;
            result.ActivityLabel = "Sample operations";
            result.ActivityNote = "Sample organization activity for this demo; not measured provider traffic.";
            result.ActivityTicks = range switch
            {
                "1h" => ["1h ago", "45m", "30m", "15m", "Now"],
                "24h" => ["24h ago", "18h", "12h", "6h", "Now"],
                "30d" => ["30d ago", "22d", "15d", "7d", "Now"],
                _ => ["7d ago", "5d", "3d", "1d", "Now"]
            };
            result.ActivityLabels = labels;
            result.ObservedAt = now;
            // The source fixture has four active application credentials. Their
            // revoked/expiry records require a producer contract
            // before the C# credential table itself can render those same rows.
            result.ActiveCredentials = 4;
            result.Regions = [new("GRU", "Not reported", "Not reported", 2), new("SJC", "Not reported", "Not reported", 2)];
            return Task.FromResult(result);
        }

        private static string Ago(TimeSpan age)
        {
            var days = (int)age.TotalDays;
            var hours = age.Hours;
            if (age > TimeSpan.Zero && age < TimeSpan.FromHours(1))
            {
                return (int)age.TotalMinutes + "m ago";
            }

            if (days == 0 && hours == 0)
            {
                return "Now";
            }

            if (days == 0)
            {
                return hours + "h ago";
            }

            return hours == 0 ? days + "d ago" : days + "d " + hours + "h ago";
        }
    }
}
