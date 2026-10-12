# TerraScale.Dashboard

Platform UI and billing, with identity owned by ts-auth and persistent state
accessed through ts-go.

The C# / RazorSlices / Datastar migration is in progress in the single-project
`TerraScale.Dashboard.slnx`. See [migration status, validation and remaining
feature parity](docs/csharp-migration.md). The C# host is not ready to replace
the existing Go production deployment.

Read the [required zero-configuration operating contract](docs/zero-configuration.md)
and [current development/runtime limitations](docs/development.md).
