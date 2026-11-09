using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Clock;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Functions;
using TerraBase.Features.Health;
using TerraBase.Features.HomeApply;
using TerraBase.Features.LocalAck;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Pressure;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Features.Shapes;
using TerraBase.Features.Sql;
using TerraBase.Features.UserAuth;

namespace TerraBase.Hosting;

internal static class TerraBaseJson
{
    public static readonly IJsonTypeInfoResolver Resolver = JsonTypeInfoResolver.Combine(
        ApiKeysJsonContext.Default,
        BackupJsonContext.Default,
        ClockJsonContext.Default,
        FileStorageJsonContext.Default,
        FunctionsJsonContext.Default,
        HealthJsonContext.Default,
        HomeApplyJsonContext.Default,
        LocalAckJsonContext.Default,
        ManagementJsonContext.Default,
        PlatformJsonContext.Default,
        PressureJsonContext.Default,
        RegionsJsonContext.Default,
        ReplicasJsonContext.Default,
        ShapesJsonContext.Default,
        SqlJsonContext.Default,
        UserAuthJsonContext.Default,
        TerraBaseJsonContext.Default);
}
