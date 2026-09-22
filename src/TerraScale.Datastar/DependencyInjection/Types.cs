namespace TerraScale.Datastar.DependencyInjection;

public sealed class PatchElementsOptions
{
    public string? Selector { get; init; }
    public ElementPatchMode PatchMode { get; init; } = Consts.DefaultElementPatchMode;
    public bool UseViewTransition { get; init; } = Consts.DefaultElementsUseViewTransitions;
    public string? ViewTransitionSelector { get; init; }
    public PatchElementNamespace Namespace { get; init; } = Consts.DefaultPatchElementNamespace;
    public string? EventId { get; init; }
    public TimeSpan Retry { get; init; } = Consts.DefaultSseRetryDuration;
}

public sealed class PatchSignalsOptions
{
    public bool OnlyIfMissing { get; init; } = Consts.DefaultPatchSignalsOnlyIfMissing;
    public string? EventId { get; init; }
    public TimeSpan Retry { get; init; } = Consts.DefaultSseRetryDuration;
}

public sealed class RemoveElementOptions
{
    public bool UseViewTransition { get; init; } = Consts.DefaultElementsUseViewTransitions;
    public string? EventId { get; init; }
    public TimeSpan Retry { get; init; } = Consts.DefaultSseRetryDuration;
}

public sealed class ExecuteScriptOptions
{
    public string? EventId { get; init; }
    public TimeSpan Retry { get; init; } = Consts.DefaultSseRetryDuration;
    public bool AutoRemove { get; init; } = true;
    public IReadOnlyList<KeyValuePair<string, string>> Attributes { get; init; } = [];
}
