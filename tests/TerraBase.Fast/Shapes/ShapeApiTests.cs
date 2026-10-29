using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Shapes;
using TerraBase.Fast.Documents;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Shapes;

/// <summary>
/// <c>GET …/collections/{collection}/shape</c> (v1 shape protocol in PR #216, sections 2, 3, 8, 9, 11) on one real node.
/// Validation, scope, listener and budget checks run on the stock server. Lineage-dependent checks register fakes for
/// the internal shape services; snapshot, live-tail and resume fixtures belong only to this test binary.
/// </summary>
public sealed class ShapeApiTests
{
    private const string Handle = "h1-0123456789abcdef";

    // Base64url of the document id 0000000000E008000000000001, as a snapshot page position.
    private const string EncodedId = "MDAwMDAwMDAwMEUwMDgwMDAwMDAwMDAwMDE";
    private const string ShapeId = "0123456789abcdef0123456789abcdef";
    private const long Snapshot = 7;
    private static readonly string[] ExpectedParameters = ["project", "database", "collection", "where", "handle", "offset", "live", "cursor", "TB-Shape-Resume", "X-TerraScale-Region-Pin"];
    private static readonly string[] ExpectedResponses = ["200", "307", "304", "400", "404", "409", "414", "429", "503"];

    // Fixture bytes the fake page returns verbatim; the endpoint must not re-serialize them.
    private static readonly byte[] FixtureBody = Encoding.UTF8.GetBytes(
        """{"shape_id":"0123456789abcdef0123456789abcdef","handle":"h1-0123456789abcdef","offset":"snap.7.0","next_offset":"7","covered_offset":"7","values_at":"7","up_to_date":false,"changes":[{"offset":"7","ops":[]}]}""");

    [Test]
    public async Task Bootstrap_redirects_to_the_newest_snapshot_with_the_handle_then_the_offset_in_canonical_order()
    {
        await using var api = await StartWithFixturesAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(reply.Location!.OriginalString).IsEqualTo($"{Shape(api, "")}?handle={Handle}&offset=snap.{Snapshot}.0");
        await Assert.That(reply.Handle).IsEqualTo(Handle);
        await CacheControlAssert.EqualAsync(reply.CacheControl);
        await Assert.That(reply.Vary).Contains("Authorization");
    }

    [Test]
    public async Task The_bootstrap_redirect_carries_the_canonical_filter_without_whitespace_and_with_a_literal_less_than_sign()
    {
        await using var api = await StartWithFixturesAsync();
        // Whitespace outside strings is removed by the canonical form; '<' inside a string stays literal in the canonical
        // JSON, and the Location percent-encodes it like every other byte of the target.
        var where = Uri.EscapeDataString("{ \"eq\" : [ \"/t\" , \"a<b\" ] }");

        var reply = await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&where={where}"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        var location = reply.Location!.OriginalString;
        await Assert.That(location.Contains(' ')).IsFalse();
        await Assert.That(location.Contains('<')).IsFalse();
        var pairs = location[(location.IndexOf('?') + 1)..].Split('&')
            .Select(pair => pair.Split('=', 2))
            .Select(parts => (Name: parts[0], Value: Uri.UnescapeDataString(parts[1])))
            .ToList();
        await Assert.That(string.Join(',', pairs.Select(pair => pair.Name))).IsEqualTo("where,handle,offset");
        await Assert.That(pairs.Single(pair => pair.Name == "where").Value).IsEqualTo("{\"eq\":[\"/t\",\"a<b\"]}");
        await Assert.That(pairs.Single(pair => pair.Name == "handle").Value).IsEqualTo(Handle);
        await Assert.That(pairs.Single(pair => pair.Name == "offset").Value).IsEqualTo($"snap.{Snapshot}.0");
    }

    [Test]
    public async Task A_snapshot_page_returns_the_fixture_bytes_verbatim_with_its_etag_and_metadata_headers()
    {
        await using var api = await StartWithFixturesAsync();
        var bootstrap = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"));

        var page = await GetShapeAsync(api.Client, bootstrap.Location!);

        await Assert.That(page.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.RawBody.AsSpan().SequenceEqual(FixtureBody)).IsTrue();
        await Assert.That(page.ContentType).IsEqualTo("application/json");
        await Assert.That(page.Handle).IsEqualTo(Handle);
        await Assert.That(page.NextOffset).IsEqualTo(Snapshot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(page.UpToDate).IsEqualTo("false");
        var digest = Convert.ToHexString(SHA256.HashData(FixtureBody)[..8]).ToLowerInvariant();
        await Assert.That(page.ETag).IsEqualTo($"\"{ShapeId}:{Handle}:snap.{Snapshot}.0:{Snapshot}:{digest}\"");
        await CacheControlAssert.EqualAsync(page.CacheControl);
    }

    [Test]
    public async Task A_stale_handle_is_409_must_refetch_with_the_current_handle_in_body_and_header_and_no_store()
    {
        await using var api = await StartWithFixturesAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, "?handle=h1-0000000000000000&offset=0"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(reply.Code).IsEqualTo("must_refetch");
        await Assert.That(reply.Json.GetProperty("error").GetProperty("handle").GetString()).IsEqualTo(Handle);
        await Assert.That(reply.Handle).IsEqualTo(Handle);
        await CacheControlAssert.EqualAsync(reply.CacheControl);
    }

    [Test]
    public async Task Bootstrap_with_a_handle_is_409_must_refetch_even_with_a_resume_header()
    {
        await using var api = await StartWithFixturesAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=-1"), ("TB-Shape-Resume", "%%%not-a-token"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(reply.Code).IsEqualTo("must_refetch");
        await Assert.That(reply.Handle).IsEqualTo(Handle);
    }

    [Test]
    public async Task The_resume_header_is_ignored_when_a_handle_is_sent_so_the_request_is_not_400()
    {
        await using var api = await StartWithFixturesAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0"), ("TB-Shape-Resume", "AQAA"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(reply.Handle).IsEqualTo(Handle);
    }

    [Test]
    public async Task Offsets_above_the_sealed_head_or_naming_no_snapshot_are_409_must_refetch()
    {
        await using var api = await StartWithFixturesAsync();

        foreach (var offset in new[] { "9999999", $"snap.{Snapshot + 1}.0" })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset={offset}"));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(reply.Code).IsEqualTo("must_refetch");
        }
    }

    [Test]
    public async Task Unknown_parameters_duplicates_and_empty_values_are_400_invalid_request()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        foreach (var query in new[] { "?offset=-1&bogus=1", "?offset=-1&offset=-1", "?offset=", "", "?offset=-1&where=" })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, query));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(reply.Code).IsEqualTo("invalid_request");
        }
    }

    [Test]
    public async Task Non_canonical_decimal_offsets_and_snapshot_pages_are_400_invalid_request()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        foreach (var offset in new[]
        {
            "01", "%2B1", "-2", "1.0", "9223372036854775808", "snap.01.0", "snap.1.",
            "snap.1.not%2Bbase64url%21", "snap.2._w", "snap.2.AA",
            // Padded, trailing and non-canonical (unused low bits set) encodings of the valid id 0000000000E008000000000001.
            $"snap.2.{EncodedId}%3D", $"snap.2.{EncodedId}.0", $"snap.2.{EncodedId[..^1]}F",
        })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset={offset}"));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(reply.Code).IsEqualTo("invalid_request");
        }
    }

    [Test]
    public async Task Live_needs_a_decimal_offset_and_the_cursor_bucket_must_stay_below_the_maximum()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        foreach (var query in new[]
        {
            "?offset=-1&live=true",
            $"?handle={Handle}&offset=0&live=false",
            $"?handle={Handle}&offset=snap.{Snapshot}.0&live=true",
            $"?handle={Handle}&offset=0&live=true&cursor=9223372036854775807",
        })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, query));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(reply.Code).IsEqualTo("invalid_request");
        }
    }

    [Test]
    public async Task A_cursor_is_only_valid_for_a_resume_or_a_live_request()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0&cursor=5"));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(reply.Code).IsEqualTo("invalid_request");
    }

    [Test]
    public async Task A_resume_envelope_that_does_not_decode_canonically_is_400_invalid_request()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        var query = await GetShapeAsync(api.Client, Shape(api, "?offset=-1&cursor=AQ%3D%3D"));
        var header = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", "AQ=="));

        await Assert.That(query.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(query.Code).IsEqualTo("invalid_request");
        await Assert.That(header.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(header.Code).IsEqualTo("invalid_request");

        var duplicate = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", "AQAA"), ("TB-Shape-Resume", "AQAA"));
        await Assert.That(duplicate.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(duplicate.Code).IsEqualTo("invalid_request");
    }

    [Test]
    public async Task A_resume_sent_with_both_the_header_and_the_cursor_is_400_invalid_request()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        var reply = await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&cursor={ResumeToken()}"), ("TB-Shape-Resume", ResumeToken()));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(reply.Code).IsEqualTo("invalid_request");
    }

    [Test]
    public async Task An_invalid_where_is_400_invalid_query_and_a_non_json_where_too()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        var notJson = await GetShapeAsync(api.Client, Shape(api, "?offset=-1&where=%7Bnope"));
        var notFilter = await GetShapeAsync(api.Client, Shape(api, "?offset=-1&where=%7B%22bogus%22%3A1%7D"));

        await Assert.That(notJson.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(notJson.Code).IsEqualTo("invalid_query");
        await Assert.That(notFilter.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(notFilter.Code).IsEqualTo("invalid_query");

        var malformedUnicode = Uri.EscapeDataString("{\"eq\":[\"/t\",\"\\ud800\"]}");
        var unicode = await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&where={malformedUnicode}"));
        await Assert.That(unicode.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(unicode.Code).IsEqualTo("invalid_query");
    }

    [Test]
    public async Task A_continuation_target_over_the_8128_budget_is_414_uri_too_long_as_json_with_no_store()
    {
        await using var api = await StartWithFixturesAsync();
        // Budget (contract step 9): path?where=<canonical, %-encoded>&handle=<19>&offset=snap.<19 digits>.<1024> must be at
        // most 8128 characters (8 KiB less 64 reserved). The client's own request is a bootstrap whose line stays under 8 KiB.
        string Where(int padding)
        {
            return "{\"eq\":[\"/t\",\"" + new string('a', padding) + "\"]}";
        }

        string Continuation(int padding)
        {
            return Shape(api, "") + "?where=" + Uri.EscapeDataString(Where(padding))
                + "&handle=" + Handle + "&offset=snap.9223372036854775807." + new string('x', 1024);
        }
        var padding = 8129 - Continuation(0).Length;
        var target = Shape(api, "?offset=-1&where=" + Uri.EscapeDataString(Where(padding)));
        await Assert.That(Continuation(padding).Length).IsEqualTo(8129);
        await Assert.That(target.Length).IsLessThan(8192);

        var allowed = await GetShapeAsync(api.Client, Shape(api, "?offset=-1&where=" + Uri.EscapeDataString(Where(padding - 1))));
        await Assert.That(Continuation(padding - 1).Length).IsEqualTo(8128);
        await Assert.That(allowed.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);

        var reply = await GetShapeAsync(api.Client, target);

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.RequestUriTooLong);
        await Assert.That(reply.Code).IsEqualTo("uri_too_long");
        await CacheControlAssert.EqualAsync(reply.CacheControl);
    }

    [Test]
    public async Task An_unknown_database_or_collection_is_404_before_the_query_string_is_checked()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();

        var database = await GetShapeAsync(api.Client, $"{api.Project}/databases/missing/collections/{DocumentApi.Collection}/shape?offset=bad");
        var collection = await GetShapeAsync(api.Client, $"{api.Collections}/missing/shape?offset=bad");

        await Assert.That(database.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(database.Code).IsEqualTo("unknown_database");
        await Assert.That(collection.Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(collection.Code).IsEqualTo("unknown_collection");
    }

    [Test]
    public async Task The_production_backend_bootstraps_a_real_lineage_and_refuses_fixture_handles()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        var bootstrap = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"));
        await Assert.That(bootstrap.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await Assert.That(bootstrap.Handle).IsNotNull();
        await Assert.That(bootstrap.Handle).IsNotEqualTo(Handle);
        foreach (var query in new[]
        {
            $"?handle={Handle}&offset=0", $"?handle={Handle}&offset=9223372036854775807",
            $"?handle={Handle}&offset=snap.0.0", $"?handle={Handle}&offset=snap.2.{EncodedId}",
            $"?handle={Handle}&offset=0&live=true",
        })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, query));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(reply.Code).IsEqualTo("must_refetch");
            await CacheControlAssert.EqualAsync(reply.CacheControl);
            await Assert.That(reply.Handle).IsEqualTo(bootstrap.Handle);
        }
    }

    [Test]
    public async Task Conditional_reads_match_weak_tags_and_bare_wildcards_but_reject_malformed_lists()
    {
        await using var api = await StartWithFixturesAsync();
        var target = Shape(api, $"?handle={Handle}&offset=snap.{Snapshot}.0");
        var page = await GetShapeAsync(api.Client, target);
        foreach (var condition in new[] { "W/" + page.ETag, "*" })
        {
            var cached = await GetShapeAsync(api.Client, target, ("If-None-Match", condition));
            await Assert.That(cached.Status).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That(cached.RawBody.Length).IsEqualTo(0);
            await Assert.That(cached.ETag).IsEqualTo(page.ETag);
            await Assert.That(cached.Handle).IsEqualTo(Handle);
            await Assert.That(cached.NextOffset).IsEqualTo(page.NextOffset);
            await CacheControlAssert.EqualAsync(cached.CacheControl, page.CacheControl);
        }

        var literal = await GetShapeAsync(api.Client, target, ("If-None-Match", "\"*\""));
        await Assert.That(literal.Status).IsEqualTo(HttpStatusCode.OK);
        var different = await GetShapeAsync(api.Client, target, ("If-None-Match", "\"different\""));
        await Assert.That(different.Status).IsEqualTo(HttpStatusCode.OK);
        foreach (var malformed in new[] { "unquoted", "*, \"other\"", "W/\"unterminated" })
        {
            var invalid = await GetShapeAsync(api.Client, target, ("If-None-Match", malformed));
            await Assert.That(invalid.Status).IsEqualTo(HttpStatusCode.BadRequest);
            await Assert.That(invalid.Code).IsEqualTo("invalid_request");
            await CacheControlAssert.EqualAsync(invalid.CacheControl);
        }

        using var writeOnly = await api.ClientWithScopesAsync("documents:write");
        var denied = await GetShapeAsync(writeOnly, target, ("If-None-Match", "*"));
        await Assert.That(denied.Status).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Canonical_resume_tokens_in_either_transport_fail_closed_until_proofs_are_wired()
    {
        await using var api = await StartWithFixturesAsync();
        var token = ResumeToken();
        var query = await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&cursor={token}"));
        var header = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", token));
        foreach (var reply in new[] { query, header })
        {
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.Conflict);
            await Assert.That(reply.Code).IsEqualTo("must_refetch");
            await Assert.That(reply.Handle).IsEqualTo(Handle);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
        }
    }

    [Test]
    public async Task A_proven_resume_redirects_to_the_local_commit_boundary_without_caching_the_redirect()
    {
        var resume = new FixtureResume();
        await using var api = await StartWithFixturesAsync(services => services.AddSingleton<IShapeResume>(resume));
        foreach (var useHeader in new[] { false, true })
        {
            var reply = useHeader
                ? await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", ResumeToken()))
                : await GetShapeAsync(api.Client, Shape(api, $"?offset=-1&cursor={ResumeToken()}"));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
            await Assert.That(reply.Handle).IsEqualTo(Handle);
            await Assert.That(reply.Location!.OriginalString).IsEqualTo(Shape(api, $"?handle={Handle}&offset=0"));
            await Assert.That(resume.Scope.Span.SequenceEqual(Enumerable.Range(1, 16).Select(value => (byte)value).ToArray())).IsTrue();
            await Assert.That(resume.Cursor!.Issued.Value).IsEqualTo(1UL);
            await Assert.That(resume.Cursor.Issuer.Value).IsEqualTo(1UL);
        }
    }

    [Test]
    public async Task A_resume_header_accepts_the_longest_issuer_and_all_256_origins()
    {
        var resume = new FixtureResume();
        await using var api = await StartWithFixturesAsync(services => services.AddSingleton<IShapeResume>(resume));
        var machine = new string('a', 63);
        var token = ResumeToken(machine, ChangeCursor.MaxOrigins);

        var reply = await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", token));

        await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.TemporaryRedirect);
        await CacheControlAssert.EqualAsync(reply.CacheControl);
        await Assert.That(resume.Cursor!.Machine).IsEqualTo(machine);
        await Assert.That(resume.Cursor.Stamps.Count).IsEqualTo(ChangeCursor.MaxOrigins);
    }

    [Test]
    public async Task Live_buckets_advance_past_the_input_or_use_the_clock_and_are_preserved_on_304()
    {
        await using var api = await StartWithFixturesAsync(services =>
        {
            services.AddSingleton<TimeProvider>(new FixtureClock());
            services.AddSingleton<IShapeLiveTail>(new FixtureLiveTail());
        });
        foreach (var (query, expected) in new[] { ("", "50"), ("&cursor=3", "50"), ("&cursor=60", "61") })
        {
            var target = Shape(api, $"?handle={Handle}&offset=0&live=true{query}");
            var reply = await GetShapeAsync(api.Client, target, ("TB-Shape-Resume", "ignored"));
            await Assert.That(reply.Status).IsEqualTo(HttpStatusCode.OK);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target, UriKind.Relative));
            request.Headers.TryAddWithoutValidation("If-None-Match", reply.ETag);
            using var cached = await api.Client.SendAsync(request);
            await Assert.That(cached.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That(cached.Headers.GetValues("TB-Shape-Cursor").Single()).IsEqualTo(expected);
            await Assert.That(cached.Headers.GetValues("TB-Shape-Up-To-Date").Single()).IsEqualTo("true");
            await Assert.That(cached.Headers.GetValues("TB-Shape-Handle").Single()).IsEqualTo(Handle);
        }
    }

    [Test]
    public async Task Every_origin_answer_including_401_and_403_is_private_no_store_and_never_shared_cacheable()
    {
        await using var api = await StartWithFixturesAsync(services =>
        {
            services.AddSingleton<IShapeLiveTail>(new FixtureLiveTail());
            services.AddSingleton<IShapeResume>(new FixtureResume());
        });
        using var anonymous = api.Management.DataClient();
        using var writeOnly = await api.ClientWithScopesAsync("documents:write");
        var page = Shape(api, $"?handle={Handle}&offset=snap.{Snapshot}.0");
        var etag = (await GetShapeAsync(api.Client, page)).ETag!;
        var answers = new (HttpStatusCode Status, ShapeReply Reply)[]
        {
            (HttpStatusCode.TemporaryRedirect, await GetShapeAsync(api.Client, Shape(api, "?offset=-1"))),
            (HttpStatusCode.TemporaryRedirect, await GetShapeAsync(api.Client, Shape(api, "?offset=-1"), ("TB-Shape-Resume", ResumeToken()))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, page)),
            (HttpStatusCode.NotModified, await GetShapeAsync(api.Client, page, ("If-None-Match", etag))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0"))),
            (HttpStatusCode.OK, await GetShapeAsync(api.Client, Shape(api, $"?handle={Handle}&offset=0&live=true"))),
            (HttpStatusCode.Conflict, await GetShapeAsync(api.Client, Shape(api, "?handle=h1-0000000000000000&offset=0"))),
            (HttpStatusCode.BadRequest, await GetShapeAsync(api.Client, Shape(api, "?offset=bad"))),
            (HttpStatusCode.NotFound, await GetShapeAsync(api.Client, $"{api.Collections}/missing/shape?offset=-1")),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, page, ("If-None-Match", etag))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, page, ("If-None-Match", etag))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, Shape(api, "/keys?handle=" + Handle))),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, Shape(api, "/keys?handle=" + Handle))),
            (HttpStatusCode.Unauthorized, await GetShapeAsync(anonymous, Shape(api, "/manifest?handle=" + Handle + "&offset=0"))),
            (HttpStatusCode.Forbidden, await GetShapeAsync(writeOnly, Shape(api, "/manifest?handle=" + Handle + "&offset=0"))),
            (HttpStatusCode.BadRequest, await GetShapeAsync(api.Client, Shape(api, "/manifest?offset=bad"))),
        };

        foreach (var (status, reply) in answers)
        {
            await Assert.That(reply.Status).IsEqualTo(status);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
        }
    }

    [Test]
    [Arguments(429)]
    [Arguments(503)]
    [Arguments(500)]
    public async Task Origin_failures_remain_private_no_store_even_when_the_error_writer_clears_headers(int status)
    {
        await using var api = await StartWithFixturesAsync(services =>
            services.AddSingleton<IShapeChunks>(new FailingChunks(status)));

        foreach (var suffix in new[] { "", "/manifest" })
        {
            var reply = await GetShapeAsync(api.Client, Shape(api, $"{suffix}?handle={Handle}&offset=0"));
            await Assert.That((int)reply.Status).IsEqualTo(status);
            await CacheControlAssert.EqualAsync(reply.CacheControl);
        }
    }

    [Test]
    public async Task The_shape_needs_documents_read_and_is_not_served_on_the_internal_listener()
    {
        await using var api = await DocumentApi.StartIsolatedAsync();
        using var writeOnly = await api.ClientWithScopesAsync("documents:write");

        var denied = await GetShapeAsync(writeOnly, Shape(api, "?offset=-1"));
        var internalListener = await GetShapeAsync(api.Server.Client, Shape(api, "?offset=-1"));

        await Assert.That(denied.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(denied.Code).IsEqualTo("forbidden_scope");
        await Assert.That(internalListener.Status).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task The_openapi_document_describes_getShape_with_its_parameters_scope_and_responses()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        using var json = JsonDocument.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var get = json.RootElement.GetProperty("paths")
            .GetProperty("/v1/projects/{project}/databases/{database}/collections/{collection}/shape")
            .GetProperty("get");

        await Assert.That(get.GetProperty("operationId").GetString()).IsEqualTo("getShape");
        await Assert.That(get.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString())).Contains("Live");
        // Bearer scopes live in x-required-scopes; the security requirement only names the scheme.
        await Assert.That(get.GetProperty("x-required-scopes").EnumerateArray().Select(scope => scope.GetString())).Contains("documents:read");
        await Assert.That(get.GetProperty("security").EnumerateArray().SelectMany(requirement => requirement.EnumerateObject()).Select(scheme => scheme.Name))
            .Contains("TsAuth");
        var parameters = get.GetProperty("parameters").EnumerateArray().Select(parameter => parameter.GetProperty("name").GetString()!).ToList();
        await Assert.That(parameters).IsEquivalentTo(ExpectedParameters);
        var responses = get.GetProperty("responses").EnumerateObject().Select(response => response.Name).ToList();
        await Assert.That(ExpectedResponses.Where(code => !responses.Contains(code))).IsEmpty();
    }

    /// <summary>A tenant database whose shape lineage, snapshot pages and closed chunks are fixtures (isolated backend fixtures).</summary>
    private static Task<DocumentApi> StartWithFixturesAsync(Action<IServiceCollection>? services = null) => DocumentApi.StartAsync(services: collection =>
    {
        collection.AddSingleton<IShapeLineages>(new FixtureLineages());
        collection.AddSingleton<IShapeSnapshots>(new FixtureSnapshots());
        collection.AddSingleton<IShapeChunks>(new FixtureChunks());
        services?.Invoke(collection);
    });

    private static string ResumeToken(string? machine = null, int origins = 0)
    {
        var cursor = new FeedCursor(Enumerable.Range(1, origins).ToDictionary(value => new ReplicaId((ulong)value), _ => new Hlc(1)), new Hlc(1))
        {
            Issuer = new ReplicaId(1),
            Machine = machine,
        };
        var inner = System.Buffers.Text.Base64Url.DecodeFromChars(ChangeCursor.Encode(cursor));
        var bytes = new byte[17 + inner.Length];
        bytes[0] = 1;
        for (var index = 1; index <= 16; index++)
        {
            bytes[index] = (byte)index;
        }

        inner.CopyTo(bytes.AsSpan(17));
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    private static string Shape(DocumentApi api, string query) => $"{api.Collections}/{DocumentApi.Collection}/shape{query}";

    private static async Task<ShapeReply> GetShapeAsync(HttpClient client, string uri, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request);
        return await ShapeReply.ReadAsync(response);
    }

    private static Task<ShapeReply> GetShapeAsync(HttpClient client, Uri uri) => GetShapeAsync(client, uri.OriginalString);

    private sealed class FixtureLineages : IShapeLineages
    {
        public ValueTask<LineageResult> ResolveAsync(ShapeScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(new ShapeLineage(ShapeId, Handle));

        ValueTask<LineageResult> IShapeLineages.ResolveHandleAsync(TenantContext database, CollectionInfo collection, string handle, CancellationToken cancellationToken) =>
            ValueTask.FromResult<LineageResult>(new ShapeLineage(ShapeId, Handle));
    }

    private sealed class FixtureSnapshots : IShapeSnapshots
    {
        public ValueTask<SnapshotResult> NewestAsync(ShapeScope scope, ShapeLineage lineage, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SnapshotResult>(Snapshot);

        public ValueTask<ShapeRead> PageAsync(ShapeScope scope, ShapeLineage lineage, long snapshot, string? afterId, CancellationToken cancellationToken) =>
            snapshot == Snapshot
                ? ValueTask.FromResult<ShapeRead>(new ShapeBody(FixtureBody, Snapshot.ToString(System.Globalization.CultureInfo.InvariantCulture), false))
                : ValueTask.FromResult<ShapeRead>(new MustRefetch("snapshot is not published for this handle"));
    }

    private sealed class FixtureChunks : IShapeChunks
    {
        public ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
            offset > Snapshot
                ? ValueTask.FromResult<ShapeRead>(new MustRefetch("offset is above the sealed head"))
                : ValueTask.FromResult<ShapeRead>(new ShapeBody(FixtureBody, offset.ToString(System.Globalization.CultureInfo.InvariantCulture), false));
    }

    private sealed class FailingChunks(int status) : IShapeChunks
    {
        public ValueTask<ShapeRead> ReadAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
            status switch
            {
                429 => ValueTask.FromResult<ShapeRead>(new QuotaExceeded("shape is busy", "shape_busy")),
                503 => ValueTask.FromResult<ShapeRead>(new Unavailable("shape is unavailable")),
                _ => throw new InvalidOperationException("synthetic shape failure"),
            };
    }

    private sealed class FixtureResume : IShapeResume
    {
        public ReadOnlyMemory<byte> Scope { get; private set; }

        public TerraBase.Engine.FeedCursor? Cursor { get; private set; }

        public ValueTask<ResumeResult> ResolveAsync(
            ShapeScope scope, ShapeLineage lineage, ReadOnlyMemory<byte> scope16, TerraBase.Engine.FeedCursor cursor, CancellationToken cancellationToken)
        {
            Scope = scope16;
            Cursor = cursor;
            return ValueTask.FromResult<ResumeResult>(0L);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(1000);
    }

    private sealed class FixtureLiveTail : IShapeLiveTail
    {
        public ValueTask<ShapeRead> WaitAsync(ShapeScope scope, ShapeLineage lineage, long offset, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ShapeRead>(new ShapeBody(JsonSerializer.SerializeToUtf8Bytes(
                new ShapeResponse(ShapeId, Handle, "0", "0", "0", "0", true, ResumeToken(), []), ShapeJsonContext.Default.ShapeResponse), "0", true));
    }

    private sealed record ShapeReply(
        HttpStatusCode Status, string? Handle, string? CacheControl, string? Vary, string? ETag, string? ContentType,
        string? NextOffset, string? UpToDate, Uri? Location, byte[] RawBody, JsonElement Json)
    {
        public string? Code => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("error", out var error)
            ? error.GetProperty("code").GetString()
            : null;

        public static async Task<ShapeReply> ReadAsync(HttpResponseMessage response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync();
            JsonElement json = default;
            if (bytes.Length > 0 && bytes[0] == '{')
            {
                using var document = JsonDocument.Parse(bytes);
                json = document.RootElement.Clone();
            }

            return new ShapeReply(
                response.StatusCode,
                Header(response, "TB-Shape-Handle"),
                Header(response, "Cache-Control"),
                Header(response, "Vary"),
                response.Headers.ETag?.ToString(),
                response.Content.Headers.ContentType?.MediaType,
                Header(response, "TB-Shape-Next-Offset"),
                Header(response, "TB-Shape-Up-To-Date"),
                response.Headers.Location,
                bytes,
                json);
        }

        // Raw wire values: HttpClient reorders parsed headers such as Cache-Control.
        private static string? Header(HttpResponseMessage response, string name) =>
            response.Headers.NonValidated.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;
    }
}
