namespace TerraScale.EntityFrameworkCore.Migrations;

public sealed class TerraBaseMigrationConflictException : Exception
{
    public TerraBaseMigrationConflictException()
        : base("Another migrator applied migrations while this one was running.")
    {
    }

    public TerraBaseMigrationConflictException(string message)
        : base(message)
    {
    }

    public TerraBaseMigrationConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
