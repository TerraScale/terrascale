namespace TerraBase.Engine;

/// <summary>A query requires an index or uses an unsupported search expression.</summary>
public sealed class QueryValidationException(string message) : Exception(message);
