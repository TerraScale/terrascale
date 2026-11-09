using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace TerraScale.EntityFrameworkCore.Migrations;

internal sealed class TerraBaseMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, IRelationalAnnotationProvider migrationsAnnotations)
    : SqliteMigrationsSqlGenerator(dependencies, migrationsAnnotations)
{
    private const string ForeignKeysOff = "PRAGMA foreign_keys = 0;";
    private const string ForeignKeysOn = "PRAGMA foreign_keys = 1;";
    private bool rebuilding;

    protected override void Generate(SqlOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        switch (operation.Sql)
        {
            case ForeignKeysOff:
                rebuilding = true;
                return;
            case ForeignKeysOn:
                rebuilding = false;
                return;
            default:
                base.Generate(operation, model, builder);
                return;
        }
    }

    protected override void Generate(DropTableOperation operation, IModel? model, MigrationCommandListBuilder builder, bool terminate = true)
    {
        if (rebuilding && model?.GetRelationalModel().FindTable(operation.Name, operation.Schema) is { } table && table.ReferencingForeignKeyConstraints.Any())
        {
            throw new NotSupportedException(
                $"This migration rebuilds table '{operation.Name}', which other tables reference. TerraBase always enforces foreign keys, so dropping the old table "
                + "would delete or refuse the referencing rows. Change the table in steps that run in place (add columns and indexes), or write the change as explicit SQL in the migration.");
        }

        base.Generate(operation, model, builder, terminate);
    }
}
