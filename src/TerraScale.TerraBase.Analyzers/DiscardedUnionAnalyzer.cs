using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TerraScale.TerraBase.Analyzers;

/// <summary>
/// TB0001: a union value is a result, and expected failures are union cases, so dropping one silently ignores a
/// failure. Reports a call, <c>await</c> or creation used as a statement whose value is a union, or a task of one
/// (docs/error-handling.md). <c>_ = …</c> discards it explicitly.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DiscardedUnionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The rule.</summary>
    public static readonly DiagnosticDescriptor Rule = new(
        "TB0001",
        "A union result is discarded",
        "The {0} result is discarded; handle every case in a switch, or discard it explicitly with '_ ='",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Expected failures are union cases (docs/error-handling.md); a discarded union ignores them.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.ExpressionStatement);
    }

    // Wrappers whose value is the wrapped result: x?.M() is a Nullable<T>, a task may be configured, in any order.
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

    // ponytail: a fixed depth bounds self-referential and ever-expanding types (class R<T> : Task<R<R<T>>>);
    // a union nested deeper than this many wrappers or constraints is not reported.
    private const int MaxDepth = 16;

    // Whether the value is a union, or wraps one (directly, through a type parameter's constraints or a base task).
    private static bool HoldsUnion(ITypeSymbol type, int depth) =>
        depth > 0
        && (type is ITypeParameterSymbol parameter
            ? parameter.ConstraintTypes.Any(constraint => HoldsUnion(constraint, depth - 1))
            : IsUnion(type) || (WrapperOf(type) is { } wrapper && HoldsUnion(wrapper.TypeArguments[0], depth - 1)));

    // The wrapper the type is or derives from (class ResultTask : Task<Union>), if any.
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

    // The compiler marks every union declaration with [Union] and implements IUnion on it.
    private static bool IsUnion(ITypeSymbol type) =>
        IsUnionInterface(type)
        || type.AllInterfaces.Any(IsUnionInterface)
        || type.GetAttributes().Any(attribute => attribute.AttributeClass is { Name: "UnionAttribute" } union && InCompilerServices(union));

    private static bool IsUnionInterface(ITypeSymbol type) => type is { Name: "IUnion", TypeKind: TypeKind.Interface } && InCompilerServices(type);

    private static bool InCompilerServices(ITypeSymbol type) =>
        type.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices";
}
