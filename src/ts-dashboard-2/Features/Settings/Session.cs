namespace TerraScale.Dashboard2;

public sealed record Session(
    string Id,
    string Device,
    string Ip,
    string Location,
    string CreatedAt,
    string LastActiveAt,
    string Method,          // password | github | passkey
    bool Current);
