using Microsoft.AspNetCore.Html;

namespace TerraScale.Dashboard;

/// <summary>Shared critical resources for shell, login and standalone flows.</summary>
public static class DocumentHtml
{
    public static HtmlString Assets(bool datastar = true) => new($"""
        <link rel="icon" href="{AssetUrls.Url("/brand/logo-icon-blue.svg")}" type="image/svg+xml" />
        <link rel="preload" href="/fonts/oxanium-latin-wght-normal.woff2" as="font" type="font/woff2" crossorigin />
        <link rel="stylesheet" href="{AssetUrls.Url("/css/terrascale.css")}" fetchpriority="high" />
        {(datastar ? $"""<script type="module" src="{AssetUrls.Url("/js/datastar.js")}"></script>""" : "")}
        """);
}
