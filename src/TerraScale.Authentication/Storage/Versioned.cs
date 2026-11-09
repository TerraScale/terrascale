namespace TerraScale.Authentication.Storage;

public sealed record Versioned<T>(T Value, string Version);
