namespace TerraScale.Dashboard.Errors;

public sealed record DashboardNotFound(string Message);

public sealed record DashboardConflict(string Message);

public sealed record DashboardValidation(string Message);

public sealed record DashboardForbidden(string Message);

public sealed record DashboardQuotaExceeded(string Message);

public sealed record DashboardUnavailable(string Message);

public sealed record Done;
