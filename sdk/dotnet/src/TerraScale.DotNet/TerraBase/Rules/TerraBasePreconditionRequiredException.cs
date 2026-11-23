using System.Net;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBasePreconditionRequiredException : TerraBaseException
{
    internal TerraBasePreconditionRequiredException(HttpStatusCode status, string message, string? code, int? operation,
        Exception? inner) : base(status, message, code, operation, inner) { }
}
