#!/usr/bin/env dotnet run
#:package TUnit@1.56.18
#:package Blake3@3.0.2
#:include contracts.lib.cs

// Port of the former scripts/contracts_test.py — adversarial tests for the
// contract archive boundary.

using System.Formats.Tar;
using System.Text.Json;
using TerraScale.Ci;

public sealed class ArchiveBoundaryTests
{
    private static byte[] Archive(params (string Name, TarEntryType Kind, byte[] Payload)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new TarWriter(buffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var (name, kind, payload) in entries)
            {
                var entry = new PaxTarEntry(kind, name);
                if (kind == TarEntryType.RegularFile)
                {
                    entry.DataStream = new MemoryStream(payload);
                }
                if (kind is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    entry.LinkName = "target";
                }
                writer.WriteEntry(entry);
            }
        }
        return buffer.ToArray();
    }

    private static string? ExpectRejection(byte[] payload)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"contracts-boundary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Contracts.UnpackUrl("https://registry.invalid/v1.tar", directory, _ => payload);
            return "expected ContractError";
        }
        catch (ContractError)
        {
            return null;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Rejects_path_traversal()
    {
        await Assert.That(ExpectRejection(Archive(("../escape", TarEntryType.RegularFile, "x"u8.ToArray())))).IsNull();
    }

    [Test]
    public async Task Rejects_duplicate_entries()
    {
        await Assert.That(ExpectRejection(Archive(
            ("one", TarEntryType.RegularFile, "x"u8.ToArray()),
            ("one", TarEntryType.RegularFile, "y"u8.ToArray())))).IsNull();
    }

    [Test]
    [Arguments(TarEntryType.SymbolicLink)]
    [Arguments(TarEntryType.HardLink)]
    [Arguments(TarEntryType.CharacterDevice)]
    [Arguments(TarEntryType.Fifo)]
    public async Task Rejects_links_and_special_files(TarEntryType kind)
    {
        await Assert.That(ExpectRejection(Archive(("entry", kind, [])))).IsNull();
    }

    [Test]
    public async Task Rejects_oversized_entry()
    {
        // The boundary check must reject the declared size before extraction
        // reads the payload.
        var oversized = 16 * 1024 * 1024 + 1;
        await Assert.That(ExpectRejection(Archive(("oversized", TarEntryType.RegularFile, new byte[oversized])))).IsNull();
    }

    [Test]
    public async Task Rejects_too_many_entries()
    {
        var entries = Enumerable.Range(0, 257)
            .Select(index => ($"entry-{index}", TarEntryType.Directory, Array.Empty<byte>()))
            .ToArray();
        await Assert.That(ExpectRejection(Archive(entries))).IsNull();
    }
}

public sealed class OpenApiMetadataTests
{
    private const string Document = """
        {
          "components": {"schemas": {"ImportJob": {"type": "object"}}},
          "paths": {
            "/imports": {"get": {"operationId": "list_imports", "responses": {
              "200": {"content": {"application/json": {"schema": {
                "type": "array", "items": {"$ref": "#/components/schemas/ImportJob"}
              }}}}
            }}},
            "/exports/{member}": {"get": {"operationId": "download_export", "responses": {
              "200": {"content": {"application/octet-stream": {"schema": {"type": "string"}}}}
            }}}
          }
        }
        """;

    [Test]
    public async Task Resolves_inline_array_and_scalar_success_schemas()
    {
        using var openapi = JsonDocument.Parse(Document);
        var (operations, _, _) = Contracts.OpenApiMetadata(openapi.RootElement);
        var byId = operations.ToDictionary(operation => operation.OperationId);
        await Assert.That(byId["list_imports"].ResponseModel).IsEqualTo("[]ImportJob");
        await Assert.That(byId["download_export"].ResponseModel).IsEqualTo("string");
    }
}
