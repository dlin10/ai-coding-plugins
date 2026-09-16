using Microsoft.AspNetCore.Mvc;

namespace Demo.Web.Cases.GroupSharedHelperManyCallers;

/// <summary>Three actions call one singleton helper that writes a field: one race reached from many roots,
/// which the report groups into one finding with an occurrence count.</summary>
public sealed class ActivityLog
{
    private string? _lastAction;

    public void Record(string action) => _lastAction = action;
}

[ApiController]
[Route("cases/group-shared-helper-many-callers")]
public sealed class ActivityController : ControllerBase
{
    private readonly ActivityLog _log;

    public ActivityController(ActivityLog log) => _log = log;

    [HttpPost("create")]
    public void Create() => _log.Record("create");

    [HttpPost("update")]
    public void Update() => _log.Record("update");

    [HttpPost("delete")]
    public void Delete() => _log.Record("delete");
}

public static class GroupSharedHelperManyCallersCase
{
    public static IServiceCollection AddGroupSharedHelperManyCallers(this IServiceCollection services) =>
        services.AddSingleton<ActivityLog>();
}
