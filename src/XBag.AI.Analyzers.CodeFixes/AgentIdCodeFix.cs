using System.Collections.Immutable;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG005: for <c>new ChatClientAgentOptions { Name = "x", ... }</c> without an <c>Id</c>, adds <c>Id = "x"</c> as the first
/// initializer. Offered only when the Name is a constant string; any other id is the developer's choice.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AgentIdCodeFix)), Shared]
public sealed class AgentIdCodeFix : CodeFixProvider
{
    private const string Title = "Use the agent Name as a stable Id";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.UnstableAgentId);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        Diagnostic diagnostic = context.Diagnostics[0];
        if (!diagnostic.Properties.TryGetValue(AgentIdAnalyzer.NameProperty, out string? name) || name is null)
        {
            return;
        }

        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not BaseObjectCreationExpressionSyntax { Initializer: { } initializer })
        {
            return;
        }

        ExpressionSyntax id = SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            SyntaxFactory.IdentifierName("Id"),
            SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(name)));

        // Copy the layout of the first initializer (same line or one per line) so the result reads like hand-written code.
        ExpressionSyntax first = initializer.Expressions[0];
        SeparatedSyntaxList<ExpressionSyntax> expressions = initializer.Expressions.Insert(0, id.WithLeadingTrivia(first.GetLeadingTrivia()));
        SyntaxToken separator = expressions.GetSeparator(0);
        SyntaxTriviaList afterComma = initializer.Expressions.SeparatorCount > 0
            ? initializer.Expressions.GetSeparator(0).TrailingTrivia
            : initializer.OpenBraceToken.TrailingTrivia.Any(SyntaxKind.EndOfLineTrivia)
                ? initializer.OpenBraceToken.TrailingTrivia
                : SyntaxFactory.TriviaList(SyntaxFactory.Space);
        expressions = expressions.ReplaceSeparator(separator, separator.WithTrailingTrivia(afterComma));

        InitializerExpressionSyntax updated = initializer.WithExpressions(expressions);
        context.RegisterCodeFix(
            CodeAction.Create(Title, _ => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(initializer, updated))), equivalenceKey: Title),
            diagnostic);
    }
}
