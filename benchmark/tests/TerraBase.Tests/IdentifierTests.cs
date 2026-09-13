using TerraBase;

internal static class IdentifierTests
{
    internal static void Run()
    {
        var invalidValues = new string?[]
        {
            null,
            string.Empty,
            " ",
            " tenant-a",
            "tenant-a ",
            "\u00a0tenant-a",
            "tenant-a\u00a0",
            "tenant\0a",
            "tenant\na",
            "\uD800",
        };

        foreach (var value in invalidValues)
        {
            AssertThrows<ArgumentException>(() => new TenantId(value!), $"tenant value {value ?? "<null>"} must be rejected");
            AssertThrows<ArgumentException>(() => new CollectionId(value!), $"collection value {value ?? "<null>"} must be rejected");
            AssertThrows<ArgumentException>(() => new DocumentKey(new TenantId("tenant-a"), new CollectionId("users"), value!), $"document value {value ?? "<null>"} must be rejected");
        }

        var exactAscii = new TenantId(new string('a', 255));
        Assert(exactAscii.Value.Length == 255, "255 UTF-8 bytes must be accepted");
        AssertThrows<ArgumentException>(() => new TenantId(new string('a', 256)), "an identifier over 255 UTF-8 bytes must be rejected");

        var exactUtf8 = new TenantId(new string('\u00e9', 127) + "a");
        Assert(exactUtf8.Value.Length == 128, "a value whose UTF-8 encoding is exactly 255 bytes must be accepted");
        AssertThrows<ArgumentException>(() => new TenantId(new string('\u00e9', 128)), "UTF-8 byte length must be used instead of UTF-16 character length");

        AssertThrows<InvalidOperationException>(() => _ = new TenantId(), "a tenant must not have a parameterless construction path");
        AssertThrows<InvalidOperationException>(() => _ = new CollectionId(), "a collection must not have a parameterless construction path");
        AssertThrows<InvalidOperationException>(() => _ = new DocumentKey(), "a document key must not have a parameterless construction path");

        var defaultTenant = default(TenantId);
        var defaultCollection = default(CollectionId);
        AssertThrows<InvalidOperationException>(() => _ = defaultTenant.Value, "default tenant values must fail closed");
        AssertThrows<InvalidOperationException>(() => _ = defaultCollection.Value, "default collection values must fail closed");
        AssertThrows<ArgumentException>(() => new DocumentKey(defaultTenant, new CollectionId("users"), "document-1"), "default tenants must not enter document keys");
        AssertThrows<ArgumentException>(() => new DocumentKey(new TenantId("tenant-a"), defaultCollection, "document-1"), "default collections must not enter document keys");

        var defaultKey = default(DocumentKey);
        AssertThrows<ArgumentException>(() => _ = defaultKey.Tenant, "default document keys must not expose a default tenant");
        AssertThrows<ArgumentException>(() => _ = defaultKey.Collection, "default document keys must not expose a default collection");
        AssertThrows<InvalidOperationException>(() => _ = defaultKey.DocumentId, "default document keys must not expose a default document");
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(message, exception);
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
