namespace TerraScale.Authentication.Features.Ui;

/// <summary>
/// Lucide icons (the shared design system's set) as inline SVG for the hosted pages. Size and the 1.5px stroke come from
/// the <c>.ts-icon</c> rules in <c>PageCss</c> and the colour from <c>currentColor</c>: the CSP allows no inline style attributes.
/// </summary>
public static class HostedIcons
{
    /// <summary>Lucide <c>arrow-left</c>.</summary>
    public const string ArrowLeft = Open + """<path d="m12 19-7-7 7-7"/><path d="M19 12H5"/></svg>""";

    /// <summary>Lucide <c>circle-alert</c>.</summary>
    public const string CircleAlert = Open + """<circle cx="12" cy="12" r="10"/><line x1="12" x2="12" y1="8" y2="12"/><line x1="12" x2="12.01" y1="16" y2="16"/></svg>""";

    /// <summary>Lucide <c>circle-check</c>.</summary>
    public const string CircleCheck = Open + """<circle cx="12" cy="12" r="10"/><path d="m16 9-5.5 5.5L8 12"/></svg>""";

    /// <summary>Lucide <c>shield-check</c>.</summary>
    public const string ShieldCheck = Open + """<path d="M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"/><path d="m9 12 2 2 4-4"/></svg>""";

    private const string Open = """<svg class="ts-icon ts-icon-sm" viewBox="0 0 24 24" aria-hidden="true" focusable="false">""";
}
