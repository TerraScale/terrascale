using System.Text;

namespace TerraBase;

internal static class IdentifierValidation
{
    internal const int MaximumUtf8Bytes = 255;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string Validate(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0)
        {
            throw new ArgumentException($"{parameterName} must not be empty.", parameterName);
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException($"{parameterName} must not have surrounding whitespace.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} contains a control character.", parameterName);
        }

        try
        {
            if (StrictUtf8.GetByteCount(value) > MaximumUtf8Bytes)
            {
                throw new ArgumentException($"{parameterName} exceeds {MaximumUtf8Bytes} UTF-8 bytes.", parameterName);
            }
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"{parameterName} is not valid UTF-8.", parameterName, exception);
        }

        return value;
    }
}

/// <summary>Identifies an owning customer tenant.</summary>
public readonly record struct TenantId
{
    private readonly string? value;

    /// <summary>Rejects construction without a validated tenant value.</summary>
    public TenantId() => throw new InvalidOperationException("A tenant identifier requires a value.");

    /// <summary>Creates a tenant identity.</summary>
    /// <param name="value">The non-empty tenant value.</param>
    public TenantId(string value)
    {
        this.value = IdentifierValidation.Validate(value, nameof(value));
    }

    /// <summary>The stable tenant value.</summary>
    public string Value => value ?? throw new InvalidOperationException("The tenant identifier is uninitialized.");

    /// <summary>Returns the wire representation.</summary>
    public override string ToString() => Value;

    internal void EnsureValid(string parameterName) => IdentifierValidation.Validate(value, parameterName);
}

/// <summary>Identifies one collection inside a tenant.</summary>
public readonly record struct CollectionId
{
    private readonly string? value;

    /// <summary>Rejects construction without a validated collection value.</summary>
    public CollectionId() => throw new InvalidOperationException("A collection identifier requires a value.");

    /// <summary>Creates a collection identity.</summary>
    /// <param name="value">The non-empty collection value.</param>
    public CollectionId(string value)
    {
        this.value = IdentifierValidation.Validate(value, nameof(value));
    }

    /// <summary>The stable collection value.</summary>
    public string Value => value ?? throw new InvalidOperationException("The collection identifier is uninitialized.");

    /// <summary>Returns the wire representation.</summary>
    public override string ToString() => Value;

    internal void EnsureValid(string parameterName) => IdentifierValidation.Validate(value, parameterName);
}

/// <summary>Identifies one document inside a tenant collection.</summary>
public readonly record struct DocumentKey
{
    private readonly TenantId tenant;
    private readonly CollectionId collection;
    private readonly string? documentId;

    /// <summary>Rejects construction without all validated key components.</summary>
    public DocumentKey() => throw new InvalidOperationException("A document key requires tenant, collection, and document values.");

    /// <summary>Creates a document key.</summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="documentId">The document identifier.</param>
    public DocumentKey(TenantId tenant, CollectionId collection, string documentId)
    {
        tenant.EnsureValid(nameof(tenant));
        collection.EnsureValid(nameof(collection));
        this.tenant = tenant;
        this.collection = collection;
        this.documentId = IdentifierValidation.Validate(documentId, nameof(documentId));
    }

    /// <summary>The tenant owning the document.</summary>
    public TenantId Tenant
    {
        get
        {
            tenant.EnsureValid(nameof(Tenant));
            return tenant;
        }
    }

    /// <summary>The document collection.</summary>
    public CollectionId Collection
    {
        get
        {
            collection.EnsureValid(nameof(Collection));
            return collection;
        }
    }

    /// <summary>The document identifier.</summary>
    public string DocumentId => documentId ?? throw new InvalidOperationException("The document identifier is uninitialized.");
}
