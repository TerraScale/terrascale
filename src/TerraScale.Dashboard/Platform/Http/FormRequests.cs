using System.Text.Json;

namespace TerraScale.Dashboard.Platform.Http
{
    public sealed record FormReadRefusal(int Status, string Message);

    public readonly union FormReadOutcome(IFormCollection, FormReadRefusal);

    public static class FormRequests
    {
        public const string UnsupportedDetail = "Send this form as application/x-www-form-urlencoded or multipart/form-data; use the /v1 API for JSON.";

        public static JsonDocument? TryParseJson(string payload, JsonDocumentOptions options)
        {
            try
            {
                return JsonDocument.Parse(payload, options);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static async Task<IFormCollection> ReadAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            var outcome = await TryReadAsync(http, cancellationToken);
            return outcome switch
            {
                IFormCollection form => form,
                FormReadRefusal refusal => throw new AccountAccessException(refusal.Message, refusal.Status),
            };
        }

        public static async Task<FormReadOutcome> TryReadAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            if (!http.Request.HasFormContentType)
            {
                return new FormReadRefusal(StatusCodes.Status415UnsupportedMediaType, UnsupportedDetail);
            }

            try
            {
                return await http.Request.ReadFormAsync(cancellationToken);
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status400BadRequest)
            {
                return new FormReadRefusal(StatusCodes.Status400BadRequest, "The form body is malformed.");
            }
            catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return new FormReadRefusal(StatusCodes.Status413PayloadTooLarge, "The form submission is too large.");
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                return new FormReadRefusal(StatusCodes.Status400BadRequest, "The form body is malformed.");
            }
            catch (InvalidDataException)
            {
                return new FormReadRefusal(StatusCodes.Status400BadRequest, "The form body is malformed.");
            }
        }
    }
}
