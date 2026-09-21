namespace TerraScale.Auth.Templates;

internal static class TemplatesModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateJwtTemplateEndpoint),
        typeof(ListJwtTemplatesEndpoint),
        typeof(UpdateJwtTemplateEndpoint),
        typeof(DeleteJwtTemplateEndpoint),
        typeof(MintTokenEndpoint),
    ];
}
