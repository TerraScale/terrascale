namespace TerraScale.Endpoints.Binding;

public sealed record Validation(string Message);

public sealed record UnsupportedMediaType(string Message);

public readonly union Bound<T>(T, Validation, UnsupportedMediaType);
