using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>The server requires If-Match or If-None-Match for a Rules mutation (428).</summary>
public sealed class TerraBasePreconditionRequiredException : TerraBaseException
{
    internal TerraBasePreconditionRequiredException(HttpStatusCode status, string message, string? code, int? operation,
        Exception? inner) : base(status, message, code, operation, inner) { }
}
