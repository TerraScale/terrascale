namespace TerraScale.Dashboard.Platform.Http
{
    public static class FormRequests
    {
        public const string UnsupportedDetail = "Send this form as application/x-www-form-urlencoded or multipart/form-data; use the /v1 API for JSON.";

        public static async Task<IFormCollection> ReadAsync(HttpContext http, CancellationToken cancellationToken = default)
        {
            if (!http.Request.HasFormContentType)
            {
                throw new AccountAccessException(UnsupportedDetail, StatusCodes.Status415UnsupportedMediaType);
            }

            return await http.Request.ReadFormAsync(cancellationToken);
        }
    }
}
