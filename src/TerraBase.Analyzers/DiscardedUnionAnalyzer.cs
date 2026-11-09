using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TerraBase.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedUnionAnalyzer : DiagnosticAnalyzer
{
    public static readonly DiagnosticDescriptor Rule = new(
        "TB0001",
        "A union result is discarded",
        "The {0} result is discarded; handle every case in a switch, or discard it explicitly with '_ ='",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Expected failures are union cases (docs/error-handling.md); a discarded union ignores them.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.ExpressionStatement);
    }

    private static readonly ImmutableHashSet<string> Wrappers =
    [
        "System.Nullable`1",
        "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask`1",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1",
    ];

    private static void Analyze(OperationAnalysisContext context)
    {
        var value = ((IExpressionStatementOperation)context.Operation).Operation;
        if (value is IAssignmentOperation or IIncrementOrDecrementOperation || value.Type is not { } type)
        {
            return;
        }

        if (HoldsUnion(type, MaxDepth))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, value.Syntax.GetLocation(), type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    private const int MaxDepth = 16;

    private static bool HoldsUnion(ITypeSymbol type, int depth) =>
        depth > 0
        && (type is ITypeParameterSymbol parameter
            ? parameter.ConstraintTypes.Any(constraint => HoldsUnion(constraint, depth - 1))
            : IsUnion(type) || (WrapperOf(type) is { } wrapper && HoldsUnion(wrapper.TypeArguments[0], depth - 1)));

    private static INamedTypeSymbol? WrapperOf(ITypeSymbol type)
    {
        for (var candidate = type as INamedTypeSymbol; candidate is not null; candidate = candidate.BaseType)
        {
            if (candidate.TypeArguments.Length == 1
                && Wrappers.Contains($"{candidate.ContainingNamespace?.ToDisplayString()}.{candidate.MetadataName}"))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsUnion(ITypeSymbol type) =>
        IsUnionInterface(type)
        || type.AllInterfaces.Any(IsUnionInterface)
        || type.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "UnionAttribute" } union && InCompilerServices(union));

    private static bool IsUnionInterface(ITypeSymbol type) => type is { Name: "IUnion", TypeKind: TypeKind.Interface } && InCompilerServices(type);

    private static bool InCompilerServices(ITypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
}
