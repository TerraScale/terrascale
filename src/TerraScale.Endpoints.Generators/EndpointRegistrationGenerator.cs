using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TerraScale.Endpoints.Generators;

/// <summary>
/// Finds every concrete <c>TerraScale.Endpoints.IEndpoint</c> class in the compilation and emits
/// <c>GeneratedEndpoints.All</c> plus a reflection-free <c>AddTerraScaleEndpoints()</c> overload.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EndpointRegistrationGenerator : IIncrementalGenerator
{
    private const string EndpointInterface = "TerraScale.Endpoints.IEndpoint";
    private const string RegistrationType = "TerraScale.Endpoints.EndpointRegistration";

    private static readonly DiagnosticDescriptor InaccessibleEndpoint = new(
        id: "TSE001",
        title: "Endpoint is not accessible to generated registration",
        messageFormat: "Endpoint '{0}' is private or protected, so it cannot be registered automatically; make it public or internal",
        category: "TerraScale.Endpoints",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor GenericEndpoint = new(
        id: "TSE002",
        title: "Generic endpoint cannot be registered automatically",
        messageFormat: "Endpoint '{0}' is an open generic type; register a closed type with EndpointRegistration.Create<T>()",
        category: "TerraScale.Endpoints",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var referencesLibrary = context.CompilationProvider
            .Select(static (compilation, _) => compilation.GetTypeByMetadataName(RegistrationType) is not null);

        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (syntax, cancellationToken) => Inspect(syntax, cancellationToken))
            .Where(static candidate => candidate is not null)
            .Select(static (candidate, _) => candidate!)
            .Collect();

        context.RegisterSourceOutput(candidates.Combine(referencesLibrary), static (output, input) =>
        {
            var (found, enabled) = input;
            if (!enabled)
            {
                return;
            }

            var endpoints = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var candidate in found)
            {
                if (candidate.Problem is { } problem)
                {
                    output.ReportDiagnostic(Diagnostic.Create(problem, candidate.Location?.ToLocation(), candidate.DisplayName));
                }
                else
                {
                    endpoints.Add(candidate.FullyQualifiedName);
                }
            }

            output.AddSource("TerraScale.Endpoints.g.cs", SourceText.From(Emit(endpoints), Encoding.UTF8));
        });
    }

    private static Candidate? Inspect(GeneratorSyntaxContext syntax, CancellationToken cancellationToken)
    {
        if (syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, cancellationToken) is not INamedTypeSymbol type
            || type.IsAbstract
            || type.IsStatic
            || !type.AllInterfaces.Any(static item => item.ToDisplayString() == EndpointInterface))
        {
            return null;
        }

        // Partial classes surface once per declaration; only the first declaration reports.
        if (type.DeclaringSyntaxReferences.Length > 1 && type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) != syntax.Node)
        {
            return null;
        }

        DiagnosticDescriptor? problem = null;
        if (IsGeneric(type))
        {
            problem = GenericEndpoint;
        }
        else if (!IsAccessible(type))
        {
            problem = InaccessibleEndpoint;
        }

        return new Candidate(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.ToDisplayString(),
            problem,
            problem is null ? null : LocationInfo.From(syntax.Node.GetLocation()));
    }

    private static bool IsGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAccessible(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Emit(IEnumerable<string> endpoints)
    {
        var source = new StringBuilder();
        source.Append("""
            // <auto-generated/>
            #nullable enable

            namespace TerraScale.Endpoints
            {
                /// <summary>Endpoints discovered at compile time in this project.</summary>
                [global::System.CodeDom.Compiler.GeneratedCode("TerraScale.Endpoints.Generators", "3.0.0")]
                internal static class GeneratedEndpoints
                {
                    /// <summary>Every concrete endpoint in this project, in deterministic order.</summary>
                    public static global::System.Collections.Generic.IReadOnlyList<global::TerraScale.Endpoints.EndpointRegistration> All { get; } =
                        new global::TerraScale.Endpoints.EndpointRegistration[]
                        {

            """);
        foreach (var endpoint in endpoints)
        {
            source.Append("                global::TerraScale.Endpoints.EndpointRegistration.Create<").Append(endpoint).Append(">(),\n");
        }

        source.Append("""
                        };

                    /// <summary>Registers this project's endpoints and OpenAPI services without reflection scanning.</summary>
                    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddTerraScaleEndpoints(
                        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,
                        global::System.Action<global::TerraScale.Endpoints.EndpointOptions>? configure = null) =>
                        global::TerraScale.Endpoints.EndpointServiceCollectionExtensions.AddTerraScaleEndpoints(services, All, configure);
                }
            }

            """);
        return source.ToString();
    }

    private sealed record Candidate(string FullyQualifiedName, string DisplayName, DiagnosticDescriptor? Problem, LocationInfo? Location);

    private sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location) =>
            new(location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan, location.GetLineSpan().Span);

        public Location ToLocation() => Location.Create(this.FilePath, this.Span, this.LineSpan);
    }
}
