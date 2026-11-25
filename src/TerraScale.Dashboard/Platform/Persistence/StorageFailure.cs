using System.Globalization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public static class StorageFailure
{
    public static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error switch
        {
            TerraBaseSqlException sql => string.Create(CultureInfo.InvariantCulture, $"sql {sql.Code} sqlstate={sql.Sqlstate} status={(int?)sql.StatusCode}"),
            TerraBaseException sdk => string.Create(CultureInfo.InvariantCulture, $"terrabase {sdk.ErrorCode ?? "unknown"} status={(int?)sdk.StatusCode}"),
            HttpRequestException http => string.Create(CultureInfo.InvariantCulture, $"http {http.HttpRequestError} status={(int?)http.StatusCode}"),
            OperationCanceledException => "deadline",
            InvalidDataException => "schema checksum mismatch",
            _ => error.GetType().Name,
        };
    }
}
