---
title: C# quickstart
description: Create, read, update and query TerraBase documents with the .NET SDK.
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# C# quickstart

Run a complete document workflow against your TerraScale database in about ten minutes: create a collection, write a document, read it back, update it, query it and delete it.

## Prerequisites

- The .NET SDK, .NET 10 or .NET 11
- A TerraScale account, a project with a database, and an API key. Follow steps 1–4 of [Getting started](/ja/getting-started/): the key needs **Read and write** access to that database.
- The **Current project** ID (a ULID) from the dashboard's **Organization** page, and the API key. Export both:

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY   # keeps the key out of history
export TERRASCALE_PROJECT_ID=YOUR_PROJECT_ULID
export TERRASCALE_DATABASE_NAME=main                       # your database's name
```

## Install the SDK

The .NET SDK is not on NuGet yet. Build a package once from the monorepo and add it to a local feed:

```bash
git clone --depth 1 https://github.com/TerraScale/terrascale.git
cd terrascale/sdk/dotnet
dotnet pack TerraScale.DotNet.slnx --configuration Release -o ~/.nuget/packages-terrascale
```

Then create the quickstart project and reference the package (the version is whatever `dotnet pack` just wrote to the folder):

```bash
mkdir -p quickstart && cd quickstart
dotnet new console
dotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale
```

## Run the quickstart

Replace `Program.cs` with this. The program exchanges the key for a short-lived access token itself; every other operation goes through the SDK client.

```csharp
using System.Text.Json;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

public static class Program
{
    public static async Task<int> Main()
    {
        string project = Required("TERRASCALE_PROJECT_ID");
        string apiKey = Required("TERRASCALE_API_KEY");
        string databaseName = Required("TERRASCALE_DATABASE_NAME");

        // The SDK ships no key exchange: exchange the dashboard key for a
        // short-lived project access token (60 seconds) here and hand the
        // client a token provider.
        string separator = "_";
        int cut = apiKey.IndexOf(separator, StringComparison.Ordinal);
        if (cut < 1) throw new InvalidOperationException("Expected a dashboard API key.");
        using var auth = new HttpClient();
        var authClient = new TerraScaleAuthClient(auth, new TerraScaleAuthClientOptions
        {
            BaseAddress = new Uri("https://auth.terrascale.tech"),
            InstanceSlug = "terrascale",
        });
        TerraScaleAuthTokenResponse token = await authClient.Oidc.RequestClientCredentialsTokenAsync(
            apiKey[..cut], apiKey,
            scope: "documents:read documents:write", project: project);
        string accessToken = token.AccessToken
            ?? throw new InvalidOperationException("Token exchange returned no access token.");

        using var http = new HttpClient(TerraBaseHttp.CreateHandler());
        var documents = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
        {
            BaseAddress = new Uri("https://api.terrascale.tech"),
            AccessTokenProvider = _ => ValueTask.FromResult(accessToken),
        });
        var database = documents.Database(project, databaseName);
        string todos = "todos";

        // A new database has no collections; 409 means the collection already exists.
        try
        {
            await database.CreateCollectionAsync(todos);
        }
        catch (TerraBaseConflictException)
        {
        }

        // Write a document under a server-generated id.
        TerraBaseWriteResult created = await database.CreateDocumentAsync(todos,
            JsonSerializer.SerializeToElement(new { title = "Ship the quickstart", done = false }));
        Console.WriteLine($"created {created.Id} at version {created.Version}");

        // Read it back.
        TerraBaseDocument? doc = await database.GetDocumentAsync(todos, created.Id)
            ?? throw new InvalidOperationException("Created document is missing.");
        Console.WriteLine($"read {doc.Id} {doc.Document.GetProperty("title").GetString()}");

        // Update it conditionally: IfMatch rejects the write when another writer moved the document on.
        TerraBaseWriteResult updated = await database.PatchDocumentAsync(todos, created.Id,
            JsonSerializer.SerializeToElement(new { done = true }),
            TerraBasePrecondition.Matches(doc.Version));
        Console.WriteLine($"patched at version {updated.Version}");

        // Query for the change just written.
        TerraBaseDocumentPage page = await database.QueryAsync(todos, new TerraBaseQuery
        {
            Filter = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["eq"] = new object[] { "/done", true },
            }),
            Limit = 10,
        });
        foreach (TerraBaseDocument item in page.Items)
            Console.WriteLine($"query matched {item.Id} {item.Document.GetProperty("title").GetString()}");

        // Delete it.
        await database.DeleteDocumentAsync(todos, created.Id,
            TerraBasePrecondition.Matches(updated.Version));
        Console.WriteLine($"deleted {created.Id}");
        return 0;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing {name}.");
}
```

Run it with the key exported in your shell:

```bash
dotnet run
```

Expected output (ids and versions vary):

```text
created 01JDV7QZGK0DMB3W3QV2AB5CDM at version tbv1-22c40936cb275b5fd9e31f293a3883e8
read 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
patched at version tbv1-11c4ca9f65a4729652a1e907835fd792
query matched 01JDV7QZGK0DMB3W3QV2AB5CDM Ship the quickstart
deleted 01JDV7QZGK0DMB3W3QV2AB5CDM
```

## How it works

- **The key never reaches TerraBase.** The program exchanges it with ts-auth for a 60-second project access token; `AccessTokenProvider` hands the client a current one for each request. Keep the key on your server; see [authentication](/ja/guides/authentication).
- **Collections are created explicitly.** `CreateCollectionAsync` answers `409` when the name exists, which makes the quickstart safe to run twice.
- **Versions are opaque concurrency tokens.** Pass the version from a read into `TerraBasePrecondition.Matches` on the next write; TerraBase answers `412` when the document changed in between. See [errors and safe retries](/ja/guides/errors).
- **Documents are JSON.** Document ids are canonical ULIDs; filter paths are JSON Pointers such as `/done`.

## Next steps

- [SDK reference](/ja/guides/sdks) — the five SDKs and their install commands
- [JavaScript](/ja/getting-started/javascript), [Go](/ja/getting-started/go) and [Python](/ja/getting-started/python) quickstarts
- [SQL over HTTP](/ja/guides/sql-http) for the SQL interface

</div>
