namespace TerraScale.EntityFrameworkCore.Migrations;

/// <summary>
/// Another migrator applied migrations while this one was between two of its migrations. The provider's default
/// execution strategy retries the migration, which reads the history again and applies only what is still pending.
/// </summary>
public sealed class TerraBaseMigrationConflictException : Exception
{
    /// <summary>Creates the exception with a default message.</summary>
    public TerraBaseMigrationConflictException()
        : base("Another migrator applied migrations while this one was running.")
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public TerraBaseMigrationConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public TerraBaseMigrationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
