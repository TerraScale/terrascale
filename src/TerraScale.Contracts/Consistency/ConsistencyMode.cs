using System.Text.Json.Serialization;

namespace TerraScale.Contracts.Consistency;

public enum ConsistencyMode
{
    [JsonStringEnumMemberName("fast")]
    Fast,
    [JsonStringEnumMemberName("strong")]
    Strong,
}
