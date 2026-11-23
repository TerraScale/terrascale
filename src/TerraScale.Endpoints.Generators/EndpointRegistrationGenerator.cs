using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TerraScale.Endpoints.Generators;

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
            var unions = new SortedDictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
            foreach (var candidate in found)
            {
                if (candidate.Problem is { } problem)
                {
                    output.ReportDiagnostic(Diagnostic.Create(problem, candidate.Location?.ToLocation(), candidate.DisplayName));
                }
                else
                {
                    endpoints.Add(candidate.FullyQualifiedName);
                    if (candidate.UnionResponse is { } union)
                    {
                        unions[union.FullyQualifiedName] = union.CaseTypes;
                    }
                }
            }

            output.AddSource("TerraScale.Endpoints.g.cs", SourceText.From(Emit(endpoints, unions), Encoding.UTF8));
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

        var unionResponse = problem is null ? FindUnionResponse(type) : null;
        return new Candidate(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.ToDisplayString(),
            problem,
            problem is null ? null : LocationInfo.From(syntax.Node.GetLocation()),
            unionResponse);
    }

    private static UnionResponseInfo? FindUnionResponse(INamedTypeSymbol endpointType)
    {
        for (var current = endpointType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ContainingNamespace?.ToDisplayString() != "TerraScale.Endpoints")
            {
                continue;
            }

            ITypeSymbol? responseType = current.MetadataName switch
            {
                "Endpoint`2" when current.TypeArguments.Length == 2 => current.TypeArguments[1],
                "EndpointWithoutRequest`1" when current.TypeArguments.Length == 1 => current.TypeArguments[0],
                _ => null,
            };

            if (responseType is not INamedTypeSymbol named || !IsUnion(named))
            {
                return null;
            }

            var cases = ImmutableArray.CreateBuilder<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ctor in named.InstanceConstructors)
            {
                if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.Parameters.Length != 1)
                {
                    continue;
                }

                var paramType = ctor.Parameters[0].Type;
                if (SymbolEqualityComparer.Default.Equals(paramType, named))
                {
                    continue;
                }

                var formatted = paramType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (seen.Add(formatted))
                {
                    cases.Add(formatted);
                }
            }

            if (cases.Count == 0)
            {
                return null;
            }

            return new UnionResponseInfo(named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), cases.ToImmutable());
        }

        return null;
    }

    private static bool IsUnion(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(static iface =>
            iface.Name == "IUnion" && iface.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices")
        || type.GetAttributes().Any(static attr =>
            attr.AttributeClass is { Name: "UnionAttribute" } cls
            && cls.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices");

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

    private static string Emit(IEnumerable<string> endpoints, SortedDictionary<string, ImmutableArray<string>> unions)
    {
        var source = new StringBuilder();
        source.Append("""
            // <auto-generated/>
            #nullable enable

            namespace TerraScale.Endpoints
            {
                [global::System.CodeDom.Compiler.GeneratedCode("TerraScale.Endpoints.Generators", "3.0.0")]
                internal static class GeneratedEndpoints
                {
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

                    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddTerraScaleEndpoints(
                        this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services,
                        global::System.Action<global::TerraScale.Endpoints.EndpointOptions>? configure = null)
                    {
            """);

        var index = 0;
        foreach (var entry in unions)
        {
            source.Append("            global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<global::TerraScale.Endpoints.IUnionResponseWriterFactory<")
                .Append(entry.Key)
                .Append(">, UnionWriterFactory_")
                .Append(index)
                .Append(">(services);\n");
            index++;
        }

        source.Append("""
                        return global::TerraScale.Endpoints.EndpointServiceCollectionExtensions.AddTerraScaleEndpoints(services, All, configure);
                    }
            """);

        index = 0;
        foreach (var entry in unions)
        {
            var unionName = entry.Key;
            var cases = entry.Value;
            source.Append("\n        private sealed class UnionWriterFactory_")
                .Append(index)
                .Append(" : global::TerraScale.Endpoints.IUnionResponseWriterFactory<")
                .Append(unionName)
                .Append(">\n        {\n")
                .Append("            public global::TerraScale.Endpoints.IUnionResponseWriter<")
                .Append(unionName)
                .Append("> Create(\n")
                .Append("                global::System.IServiceProvider services,\n")
                .Append("                global::TerraScale.Endpoints.EndpointResponseRegistry registry,\n")
                .Append("                global::System.Action<global::System.Type?, int, string?> addProduces) =>\n")
                .Append("                new Writer(\n");
            for (var c = 0; c < cases.Length; c++)
            {
                source.Append("                    registry.Resolve<").Append(cases[c]).Append(">(services, addProduces)");
                source.Append(c + 1 < cases.Length ? ",\n" : ");\n\n");
            }

            source.Append("            private sealed class Writer(\n");
            for (var c = 0; c < cases.Length; c++)
            {
                source.Append("                global::System.Func<global::Microsoft.AspNetCore.Http.HttpContext, ")
                    .Append(cases[c])
                    .Append(", global::System.Threading.Tasks.Task> w")
                    .Append(c);
                source.Append(c + 1 < cases.Length ? ",\n" : ") : global::TerraScale.Endpoints.IUnionResponseWriter<");
            }

            source.Append(unionName).Append(">\n            {\n")
                .Append("                public global::System.Threading.Tasks.Task WriteAsync(global::Microsoft.AspNetCore.Http.HttpContext context, ")
                .Append(unionName)
                .Append(" response) =>\n")
                .Append("                    response switch\n")
                .Append("                    {\n");
            for (var c = 0; c < cases.Length; c++)
            {
                source.Append("                        ")
                    .Append(cases[c])
                    .Append(" value => w")
                    .Append(c)
                    .Append("(context, value),\n");
            }

            source.Append("                    };\n            }\n        }\n");
            index++;
        }

        source.Append("""
                }
            }

            """);
        return source.ToString();
    }

    private sealed record UnionResponseInfo(string FullyQualifiedName, ImmutableArray<string> CaseTypes)
    {
        public bool Equals(UnionResponseInfo? other) =>
            other is not null
            && string.Equals(FullyQualifiedName, other.FullyQualifiedName, StringComparison.Ordinal)
            && CaseTypes.SequenceEqual(other.CaseTypes, StringComparer.Ordinal);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(FullyQualifiedName);
    }

    private sealed record Candidate(
        string FullyQualifiedName,
        string DisplayName,
        DiagnosticDescriptor? Problem,
        LocationInfo? Location,
        UnionResponseInfo? UnionResponse);

    private sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
    {
        public static LocationInfo From(Location location)
        {
            var lineSpan = location.GetLineSpan();
            return new(lineSpan.Path, location.SourceSpan, lineSpan.Span);
        }

        public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
    }
}
