using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Platform.Http
{
    public readonly union ReadForm(IFormCollection, Validation, UnsupportedMediaType);

    public static class FormRequests
    {
        public const string UnsupportedDetail = "Send this form as application/x-www-form-urlencoded or multipart/form-data; use the /v1 API for JSON.";

        public static async Task<ReadForm> ReadAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            if (!http.Request.HasFormContentType)
            {
                return new UnsupportedMediaType(UnsupportedDetail);
            }

            try
            {
                return await http.Request.ReadFormAsync(cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status400BadRequest)
            {
                return new Validation("The form body is malformed.");
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                return new Validation("The form body is malformed.");
            }
            catch (InvalidDataException)
            {
                return new Validation("The form body is malformed.");
            }
        }

        public static IResult Refused(HttpContext http, Validation invalid) =>
            RequestError.Response(http, StatusCodes.Status400BadRequest, invalid.Message);

        public static IResult Refused(HttpContext http, UnsupportedMediaType unsupported) =>
            RequestError.Response(http, StatusCodes.Status415UnsupportedMediaType, unsupported.Message);
    }
}
