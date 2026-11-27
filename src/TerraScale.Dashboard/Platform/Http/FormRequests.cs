namespace TerraScale.Dashboard.Platform.Http
{
    public sealed record FormReadRefusal(int Status, string Message);

    public readonly union FormReadOutcome(IFormCollection, FormReadRefusal);

    public static class FormRequests
    {
        public const string UnsupportedDetail = "Send this form as application/x-www-form-urlencoded or multipart/form-data; use the /v1 API for JSON.";

        public static async Task<IFormCollection> ReadAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            return await TryReadAsync(http, cancellationToken) switch
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
                return new FormReadRefusal(StatusCodes.Status413PayloadTooLarge, "The change is too large. Send fewer edits at a time.");
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
