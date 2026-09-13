namespace TerraScale.Endpoints;

/// <summary>Optional marker for documenting an endpoint class. Discovery is attribute-free.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EndpointAttribute : Attribute
{
    /// <summary>Initializes the marker.</summary>
    public EndpointAttribute()
    {
    }
}
