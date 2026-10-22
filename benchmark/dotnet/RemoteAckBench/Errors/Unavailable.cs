namespace TerraBase.Errors
{
    /// <summary>The refusal value needed by the linked waiter, without the HTTP failure/response graph.</summary>
    /// <param name="Message">The admission refusal.</param>
    /// <param name="Code">The stable admission error code.</param>
    public sealed record Unavailable(string Message, string Code = "unavailable");
}
