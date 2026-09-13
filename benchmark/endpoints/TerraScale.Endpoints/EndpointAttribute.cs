namespace TerraScale.Endpoints;

/// <summary>Marks a concrete class as one HTTP endpoint.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EndpointAttribute : Attribute
{
    /// <summary>Initializes the marker.</summary>
    public EndpointAttribute()
    {
    }
}
