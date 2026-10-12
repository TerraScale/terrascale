namespace TerraScale.Authentication.Features.Ui;

/// <summary>
/// Lucide icons (the dashboard's set) as inline SVG for the hosted pages. They take the text colour through
/// <c>currentColor</c>: the CSP allows no inline style attributes.
/// </summary>
public static class HostedIcons
{
    /// <summary>Lucide <c>mail</c>.</summary>
    public const string Mail = Open + """<path d="m22 7-8.991 5.727a2 2 0 0 1-2.009 0L2 7"/><rect x="2" y="4" width="20" height="16" rx="2"/></svg>""";

    /// <summary>Lucide <c>key-round</c>.</summary>
    public const string KeyRound = Open + """<path d="M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z"/><circle cx="16.5" cy="7.5" r=".5" fill="currentColor"/></svg>""";

    /// <summary>Lucide <c>arrow-left</c>.</summary>
    public const string ArrowLeft = Open + """<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/></svg>""";

    /// <summary>Lucide <c>circle-alert</c>.</summary>
    public const string CircleAlert = Open + """<circle cx="12" cy="12" r="10"/><line x1="12" x2="12" y1="8" y2="12"/><line x1="12" x2="12.01" y1="16" y2="16"/></svg>""";

    /// <summary>Lucide <c>circle-check</c>.</summary>
    public const string CircleCheck = Open + """<circle cx="12" cy="12" r="10"/><path d="m16 9-5.5 5.5L8 12"/></svg>""";

    private const string Open = """<svg class="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">""";
}
