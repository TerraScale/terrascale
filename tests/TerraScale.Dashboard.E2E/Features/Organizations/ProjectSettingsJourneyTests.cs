using System.Net;

namespace TerraScale.Dashboard.E2E.Features.Organizations
{
    public sealed partial class TsAuthPlatformJourneyTests
    {
        [Test]
        [Arguments("viewer", "")]
        [Arguments("member", "")]
        [Arguments("admin", "")]
        [Arguments("admin", "viewer")]
        public async Task ProjectSettingsUsesOnlyCurrentExactProjectAuthority(string role, string organizationRole)
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("settings-user");
            var owner = platform.Principal("settings-owner");
            using var client = platform.Client();
            using (var login = await HttpJourney.LoginAsync(client, user.Email, user.Password))
            {
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var own = (await Me(client)).GetProperty("organizationId").GetString()!;
            var org = platform.Producer.AddOrganization("Shared settings", owner.Subject);
            var project = platform.Producer.AddProject(org, "Selected project", user.Subject);
            var sibling = platform.Producer.AddProject(org, "Private sibling", owner.Subject);
            // Creation grants admin explicitly; it must not remain an irrevocable creator privilege.
            platform.Producer.AddCollaborator(org, user.Subject, project, role);
            if (organizationRole.Length > 0)
            {
                platform.Producer.AddMember(org, user.Subject, user.Email, organizationRole);
            }

            var saved = new OrganizationRecord { Id = org, SpendCapCents = 987654 };
            saved.Projects[project] = new() { Id = project, Region = "GRU" };
            saved.Projects[sibling] = new() { Id = sibling, Region = "GRU" };
            await platform.StoreAsync(saved);
            var scope = "?organizationId=" + org + "&projectId=" + project;
            var url = "/api/v1/dashboard/project/rename" + scope;
            var html = await client.GetStringAsync("/project" + scope);
            await Assert.That(html).Contains("Selected project").And.Contains("Your effective project role</dt><dd>" + role);
            await Assert.That(html).DoesNotContain("987654");
            if (organizationRole.Length == 0)
            {
                await Assert.That(html).DoesNotContain(sibling).And.DoesNotContain(owner.Email).And.DoesNotContain("Private sibling");
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/project" + scope);
            var version = await platform.VersionAsync(org);
            var publications = platform.Producer.PlanPublications(org);
            var providerPath = "/v1/terrascale/organizations/" + org + "/projects/" + project;
            async Task<HttpResponseMessage> Rename(string name, string? token = null, string? placement = null) =>
                await HttpJourney.PostFormAsync(client, url, new() { ["_csrf"] = token ?? csrf, ["name"] = name, ["region"] = placement ?? "" });
            using (var malicious = await Rename("Forged", "malicious"))
            {
                await Assert.That(malicious.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }

            using (var crossSite = new HttpRequestMessage(HttpMethod.Post, url)
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf, ["name"] = "Forged" }) })
            {
                crossSite.Headers.Add("Origin", "https://attacker.example");
                using var denied = await client.SendAsync(crossSite);
                await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            }
            using (var organizationRename = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/rename" + scope,
                new() { ["_csrf"] = csrf, ["name"] = "Denied organization rename" }))
            {
                await Assert.That(organizationRename.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            using (var attempted = await Rename("Renamed project"))
            {
                await Assert.That(attempted.StatusCode).IsEqualTo(role == "admin" ? HttpStatusCode.SeeOther : HttpStatusCode.Forbidden);
            }

            if (role == "admin")
            {
                await Assert.That(html).Contains("<label class=\"ts-label\" for=\"project-name\">Project name</label>").And.Contains("Renaming project…");
                await Assert.That(await client.GetStringAsync("/project" + scope + "&renamed=1")).Contains("Project renamed.").And.Contains("Renamed project");
                using (var placement = await Rename("Wrong placement", placement: "SJC"))
                {
                    await Assert.That(placement.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                }

                foreach (var field in new[] { "backupRegion", "regions" })
                {
                    using (var placement = await HttpJourney.PostFormAsync(client, url,
                        new() { ["_csrf"] = csrf, ["name"] = "Wrong placement", [field] = "SJC" }))
                    {
                        await Assert.That(placement.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                    }
                }

                using (var invalid = await Rename("!!!"))
                {
                    await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
                    await Assert.That(await invalid.Content.ReadAsStringAsync()).Contains("value=\"!!!\"").And.Contains("characters including a letter or digit");
                }
                using (var duplicate = await Rename("Private sibling"))
                {
                    await Assert.That(duplicate.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
                    await Assert.That(await duplicate.Content.ReadAsStringAsync()).Contains("value=\"Private sibling\"").And.Contains("That name is already used");
                }
                platform.Producer.RefuseNext("PATCH", providerPath, HttpStatusCode.ServiceUnavailable, "unavailable", "Private provider text");
                using (var unavailable = await Rename("Preserved draft"))
                {
                    await Assert.That(unavailable.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
                    await Assert.That(await unavailable.Content.ReadAsStringAsync()).Contains("value=\"Preserved draft\"").And.Contains("Reload project settings").And.DoesNotContain("Private provider text");
                }
                platform.Producer.AddCollaborator(org, user.Subject, project, "viewer");
                using (var demoted = await Rename("Denied after demotion"))
                {
                    await Assert.That(demoted.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                }
            }
            else
            {
                await Assert.That(html).Contains("Read-only settings").And.DoesNotContain("id=\"project-name\"");
            }

            var mutations = platform.Producer.Mutations(org, user.Subject);
            foreach (var invalid in new[] { "?organizationId=" + own + "&projectId=" + project,
                "?organizationId=" + org + "&projectId=" + sibling })
            {
                using var denied = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/project/rename" + invalid,
                    new() { ["_csrf"] = csrf, ["name"] = "Denied" });
                await Assert.That(denied.StatusCode).IsEqualTo(organizationRole.Length > 0 && invalid.Contains(sibling, StringComparison.Ordinal)
                    ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
            }
            platform.Producer.RemoveGrant(org, user.Subject, project);
            using (var revoked = await Rename("Denied after revocation"))
            {
                await Assert.That(revoked.StatusCode).IsEqualTo(organizationRole.Length > 0 ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound);
            }

            if (organizationRole.Length == 0)
            {
                foreach (var resource in new[] { "members", "invitations", "ownership-transfer", "audit-events" })
                {
                    await Assert.That(platform.Producer.Count("GET", "/v1/terrascale/organizations/" + org + "/" + resource, user.Subject)).IsEqualTo(0);
                }

                using var organization = await client.GetAsync("/organization" + scope);
                await Assert.That(organization.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }
            await Assert.That(platform.Producer.Mutations(org, user.Subject)).IsEqualTo(mutations);
            await Assert.That(mutations).IsEqualTo(role == "admin" ? 3 : 0);
            await Assert.That(platform.Producer.PlanPublications(org)).IsEqualTo(publications);
            await Assert.That(await platform.VersionAsync(org)).IsEqualTo(version);
            await Assert.That(platform.Producer.Failures).IsEmpty();
        }

        [Test]
        public async Task OrganizationAdminKeepsExistingProjectRenameAndPlacementFlow()
        {
            await using var platform = await PlatformScenario.StartAsync();
            var user = platform.Principal("organization-admin");
            using var client = platform.Client();
            using var login = await HttpJourney.LoginAsync(client, user.Email, user.Password);
            var org = (await Me(client)).GetProperty("organizationId").GetString()!;
            var project = (await Me(client)).GetProperty("projectId").GetString()!;
            platform.Producer.AddMember(org, user.Subject, user.Email, "admin");
            platform.Producer.RemoveGrant(org, user.Subject, project);
            var csrf = await HttpJourney.CsrfAsync(client, "/organization?tab=projects");
            using var renamed = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/organization/projects/" + project + "/rename", new()
            { ["_csrf"] = csrf, ["name"] = "Organization managed", ["region"] = "GRU", ["backupRegion"] = "GRU", ["regions"] = "GRU" });
            await Assert.That(renamed.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            await Assert.That(await client.GetStringAsync("/project")).Contains("Organization managed");
            var stored = await platform.ProjectionAsync(org);
            await Assert.That(stored.Projects[project].Region).IsEqualTo("GRU");
            await Assert.That(stored.AuditEvents.Any(item => item.Action == "project.renamed")).IsTrue();
            await Assert.That(platform.Producer.Failures).IsEmpty();
        }
    }
}
