using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG002: replaces the <c>this</c> fallback with <c>base.GetService(serviceType, serviceKey)</c> in an override (the base
/// already answers for its own type and asks the inner instance), or with
/// <c>serviceKey is null &amp;&amp; serviceType.IsInstanceOfType(this) ? this : null</c> in an interface implementation.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GetServiceCodeFix)), Shared]
public sealed class GetServiceCodeFix : CodeFixProvider
{
    private const string Title = "Return this only for matching service types";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.GetServiceReturnsThis);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        Diagnostic diagnostic = context.Diagnostics[0];
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not ThisExpressionSyntax self
            || self.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } declaration
            || model?.GetDeclaredSymbol(declaration, context.CancellationToken) is not { Parameters.Length: 2 } method)
        {
            return;
        }

        string type = method.Parameters[0].Name;
        string key = method.Parameters[1].Name;
        bool callBase = method.OverriddenMethod is { IsAbstract: false };
        ExpressionSyntax replacement = SyntaxFactory.ParseExpression(callBase
            ? $"base.{method.OverriddenMethod!.Name}({type}, {key})"
            : $"{key} is null && {type}.IsInstanceOfType(this) ? this : null");

        if (!callBase && self.Parent is ConditionalExpressionSyntax or BinaryExpressionSyntax)
        {
            replacement = SyntaxFactory.ParenthesizedExpression(replacement);
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                Title,
                ct => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(self, replacement.WithTriviaFrom(self)))),
                equivalenceKey: Title),
            diagnostic);
    }
}
