using TerraBase.Configuration;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.Management.Queries;

/// <summary>Registers the SQL and saved-query part of the Management slice.</summary>
public static class QueriesExtensions
{
    internal const string SqlDescription =
        "sql (at most 16 KiB): SELECT * | field, … FROM collection [WHERE …] [ORDER BY field [ASC|DESC], … (at most 4)] [LIMIT 1 to 1000, default 100]. " +
        "WHERE combines field op literal (=, !=, <>, <, <=, >, >=), field IN (…), field IS [NOT] NULL with AND, OR, NOT and parentheses. " +
        "Literals: single-quoted strings, numbers, TRUE, FALSE. Fields: _id, _rev or dotted member paths.";

    /// <summary>Registers <see cref="IQueryService"/> and the shared <see cref="IQueryShapeTracker"/>.</summary>
    public static IServiceCollection AddTerraBaseManagementQueries(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc().AddServiceOptions<QueryShapeReceiver>(grpc => grpc.MaxReceiveMessageSize = QueryShapeForwarding.MaxMessageBytes);
        return services.AddSingleton<QueryFieldRecorder>()
            .AddSingleton<QueryShapeRecorder>()
            .AddSingleton<QueryShapeReceiver>()
            .AddSingleton<QueryShapeAdmission>()
            .AddSingleton<IQueryShapeTracker>(provider => provider.GetRequiredService<QueryShapeRecorder>())
            .AddSingleton<IQueryService, QueryService>();
    }

    /// <summary>Maps follower query-shape forwarding onto the existing peer listener.</summary>
    public static WebApplication MapTerraBaseQueryShapes(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<TerraBaseOptions>().PeerPort is not null)
        {
            app.MapGrpcService<QueryShapeReceiver>().WithMetadata(ListenerMetadata.Peer);
        }

        return app;
    }

    /// <summary>Admits query-shape receives after routing and before gRPC materializes their bodies.</summary>
    public static WebApplication UseTerraBaseQueryShapeAdmission(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseMiddleware<QueryShapeAdmission>();
        return app;
    }

    /// <summary>A saved query with its name trimmed, or the rule it breaks: a display name and SQL of the subset.</summary>
    internal static Valid<SavedQueryRequest> Validate(SavedQueryRequest request) =>
        (ManagementRoute.CheckDisplayName(new NameRequest(request.Name)), SqlSubset.Parse(request.Sql)) switch
        {
            (Validation error, _) => error,
            (_, Validation error) => error,
            (string name, SqlStatement) => request with { Name = name },
        };
}
