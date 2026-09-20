namespace TerraScale.Dashboard2;

/// <summary>Raised when another dashboard instance committed the same account world first.</summary>
public sealed class DashboardStateConflictException : Exception
{
    public DashboardStateConflictException(string subject)
        : base($"The account state changed concurrently for subject '{subject}'.") => Subject = subject;

    public string Subject { get; }
}
