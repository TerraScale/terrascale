namespace TerraScale.Dashboard.Platform.Http
{
    // Dashboard POSTs are CSRF-protected form endpoints; creating things over JSON belongs to TerraBase's /v1 API.
    // Reading a form with any other content type fails inside ASP.NET with InvalidOperationException, which would
    // surface as a 500. Every form read goes through here so a non-form body answers 415 with an explanation instead.
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
