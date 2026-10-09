using TerraBase.Errors;

namespace TerraBase.Features.Management.Indexes;

/// <summary>A collection's policy for automatic index creation and removal.</summary>
/// <param name="Collection">The collection name, which may be reserved before the collection exists.</param>
/// <param name="Enabled">Whether automatic index creation and removal are allowed.</param>
public sealed record AutoIndexPolicy(string Collection, bool Enabled);

/// <summary>A collection's automatic index policy to set.</summary>
/// <param name="Enabled">Required; false opts out of automatic index creation and removal.</param>
public sealed record AutoIndexPolicyRequest(bool? Enabled);

/// <summary>The saved policy, or why it was not saved.</summary>
public readonly union SetAutoIndexPolicyResult(AutoIndexPolicy, NotFound, Forbidden, Validation, Unavailable);

/// <summary>The policy, or why it cannot be read now.</summary>
public readonly union GetAutoIndexPolicyResult(AutoIndexPolicy, NotFound, Unavailable);
