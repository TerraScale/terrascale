namespace TerraScale.Endpoints;

/// <summary>How patched elements are applied to the target in the DOM.</summary>
public enum ElementPatchMode
{
    /// <summary>Morphs the outer HTML, preserving state. The default.</summary>
    Outer,

    /// <summary>Morphs the inner HTML only, preserving state.</summary>
    Inner,

    /// <summary>Replaces the outer HTML, resetting state.</summary>
    Replace,

    /// <summary>Inserts at the beginning inside the target.</summary>
    Prepend,

    /// <summary>Inserts at the end inside the target.</summary>
    Append,

    /// <summary>Inserts before the target as a sibling.</summary>
    Before,

    /// <summary>Inserts after the target as a sibling.</summary>
    After,

    /// <summary>Removes the target elements.</summary>
    Remove,
}

/// <summary>The namespace patched elements are created in.</summary>
public enum ElementNamespace
{
    /// <summary>Regular HTML. The default.</summary>
    Html,

    /// <summary>SVG.</summary>
    Svg,

    /// <summary>MathML.</summary>
    Mathml,
}

/// <summary>Datastar entry points: streams of Datastar events and browser signal reading.</summary>
public static class Datastar
{
    /// <summary>The query parameter carrying URL-encoded JSON signals on GET and DELETE requests.</summary>
    public const string SignalsQueryParameter = "datastar";

    /// <summary>Returns a <c>text/event-stream</c> result driven by Datastar events.</summary>
    /// <param name="handler">Writes Datastar events; completes when the stream should end.</param>
    /// <param name="serializerOptions">Signal serialization; defaults to web (camelCase) JSON.</param>
    public static IResult Stream(
        Func<DatastarClient, CancellationToken, Task> handler,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return new SseResult(
            response => new DatastarClient(response, serializerOptions ?? JsonSerializerOptions.Web),
            (client, cancellationToken) => handler((DatastarClient)client, cancellationToken));
    }

    /// <summary>Reads the signals the browser sent: the <c>datastar</c> query parameter on GET/DELETE, otherwise the JSON body.</summary>
    public static async Task<T> ReadSignalsAsync<T>(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string json;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsDelete(request.Method))
        {
            json = request.Query[SignalsQueryParameter].ToString();
        }
        else
        {
            using var reader = new StreamReader(request.Body);
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(json, JsonSerializerOptions.Web) ?? default!;
    }
}

/// <summary>Writes Datastar server-sent events following the Datastar SDK specification.</summary>
public sealed class DatastarClient(HttpResponse response, JsonSerializerOptions serializerOptions) : SseClient(response)
{
    private const string PatchElementsEventType = "datastar-patch-elements";
    private const string PatchSignalsEventType = "datastar-patch-signals";

    /// <summary>Patches HTML elements into the DOM.</summary>
    /// <param name="elements">Complete HTML elements. Each top-level element needs an ID unless <paramref name="selector"/> is set. Optional with <see cref="ElementPatchMode.Remove"/>.</param>
    /// <param name="selector">A CSS selector for the patch target.</param>
    /// <param name="mode">How the elements are applied.</param>
    /// <param name="useViewTransition">Uses the view transitions API.</param>
    /// <param name="viewTransitionSelector">A CSS selector for the view transition root.</param>
    /// <param name="namespace">The namespace elements are created in.</param>
    /// <param name="eventId">The optional SSE id for replay.</param>
    /// <param name="retry">The optional reconnection delay; the 1000&nbsp;ms default is omitted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task PatchElementsAsync(
        string? elements = null,
        string? selector = null,
        ElementPatchMode mode = ElementPatchMode.Outer,
        bool useViewTransition = false,
        string? viewTransitionSelector = null,
        ElementNamespace @namespace = ElementNamespace.Html,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        List<string> dataLines = [];
        if (selector is not null)
        {
            dataLines.Add($"selector {selector}");
        }

        if (mode is not ElementPatchMode.Outer)
        {
            dataLines.Add($"mode {WireName(mode)}");
        }

        if (useViewTransition)
        {
            dataLines.Add("useViewTransition true");
            if (viewTransitionSelector is not null)
            {
                dataLines.Add($"viewTransitionSelector {viewTransitionSelector}");
            }
        }

        if (@namespace is not ElementNamespace.Html)
        {
            dataLines.Add($"namespace {WireName(@namespace)}");
        }

        if (elements is not null)
        {
            foreach (var line in SplitLines(elements))
            {
                dataLines.Add(line.Length == 0 ? "elements" : $"elements {line}");
            }
        }

        return this.SendEventAsync(PatchElementsEventType, dataLines, eventId, retry, cancellationToken);
    }

    /// <summary>Serializes <paramref name="signals"/> and patches them into the browser signal store using JSON merge patch semantics.</summary>
    /// <param name="signals">The signal patch; a <c>null</c> value removes a signal.</param>
    /// <param name="onlyIfMissing">Only sets signals that do not exist yet.</param>
    /// <param name="eventId">The optional SSE id for replay.</param>
    /// <param name="retry">The optional reconnection delay; the 1000&nbsp;ms default is omitted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task PatchSignalsAsync(
        object? signals,
        bool onlyIfMissing = false,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(signals, serializerOptions);
        return this.PatchSignalsJsonAsync(json, onlyIfMissing, eventId, retry, cancellationToken);
    }

    /// <summary>Patches pre-serialized JSON into the browser signal store.</summary>
    public Task PatchSignalsJsonAsync(
        string signalsJson,
        bool onlyIfMissing = false,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalsJson);
        List<string> dataLines = [];
        if (onlyIfMissing)
        {
            dataLines.Add("onlyIfMissing true");
        }

        foreach (var line in SplitLines(signalsJson))
        {
            dataLines.Add($"signals {line}");
        }

        return this.SendEventAsync(PatchSignalsEventType, dataLines, eventId, retry, cancellationToken);
    }

    /// <summary>Removes the elements matching <paramref name="selector"/> from the DOM.</summary>
    public Task RemoveFragmentsAsync(
        string selector,
        bool useViewTransition = false,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        return this.PatchElementsAsync(
            elements: null,
            selector: selector,
            mode: ElementPatchMode.Remove,
            useViewTransition: useViewTransition,
            eventId: eventId,
            retry: retry,
            cancellationToken: cancellationToken);
    }

    /// <summary>Removes signals by dotted path, for example <c>user.name</c>.</summary>
    public Task RemoveSignalsAsync(
        IEnumerable<string> paths,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        JsonObject root = [];
        foreach (var path in paths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            JsonObject current = root;
            var segments = path.Split('.');
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (string.IsNullOrWhiteSpace(segment))
                {
                    throw new ArgumentException($"The signal path '{path}' contains an empty segment.", nameof(paths));
                }
                if (index == segments.Length - 1)
                {
                    current[segment] = null;
                }
                else if (current[segment] is JsonObject nested)
                {
                    current = nested;
                }
                else if (current[segment] is null)
                {
                    JsonObject created = [];
                    current[segment] = created;
                    current = created;
                }
                else
                {
                    throw new ArgumentException($"The signal path '{path}' traverses the non-object signal '{segment}'.", nameof(paths));
                }
            }
        }

        return this.PatchSignalsJsonAsync(root.ToJsonString(), eventId: eventId, retry: retry, cancellationToken: cancellationToken);
    }

    /// <summary>Executes JavaScript in the browser by patching a self-removing script tag.</summary>
    /// <param name="script">The JavaScript source.</param>
    /// <param name="autoRemove">Removes the script tag after it executes. The default.</param>
    /// <param name="attributes">Extra attributes for the script tag, for example <c>type="module"</c>.</param>
    /// <param name="eventId">The optional SSE id for replay.</param>
    /// <param name="retry">The optional reconnection delay; the 1000&nbsp;ms default is omitted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public Task ExecuteScriptAsync(
        string script,
        bool autoRemove = true,
        IEnumerable<string>? attributes = null,
        string? eventId = null,
        TimeSpan? retry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        StringBuilder tag = new("<script");
        if (attributes is not null)
        {
            foreach (var attribute in attributes)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(attribute);
                tag.Append(' ').Append(attribute);
            }
        }

        if (autoRemove)
        {
            tag.Append(" data-effect=\"el.remove()\"");
        }

        tag.Append('>').Append(script).Append("</script>");
        return this.PatchElementsAsync(
            elements: tag.ToString(),
            selector: "body",
            mode: ElementPatchMode.Append,
            eventId: eventId,
            retry: retry,
            cancellationToken: cancellationToken);
    }

    private static string WireName<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static string[] SplitLines(string value) => value.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}
