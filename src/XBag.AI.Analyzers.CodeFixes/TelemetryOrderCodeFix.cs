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
/// XBAG001: moves <c>UseOpenTelemetry(...)</c> (with its arguments) to directly after <c>UseFunctionInvocation(...)</c>. Only the
/// telemetry call moves; every other call keeps its place relative to the tool loop.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(TelemetryOrderCodeFix)), Shared]
public sealed class TelemetryOrderCodeFix : CodeFixProvider
{
    private const string Title = "Move UseOpenTelemetry() after UseFunctionInvocation()";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.TelemetryOutsideFunctionInvocation);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        Diagnostic diagnostic = context.Diagnostics[0];
        if (root is null || diagnostic.AdditionalLocations.Count == 0)
        {
            return;
        }

        InvocationExpressionSyntax? telemetry = root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<InvocationExpressionSyntax>();
        InvocationExpressionSyntax? functions = root.FindNode(diagnostic.AdditionalLocations[0].SourceSpan, getInnermostNodeForTie: true)
            .FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (telemetry?.Expression is not MemberAccessExpressionSyntax || functions?.Expression is not MemberAccessExpressionSyntax)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, ct => MoveAsync(context.Document, root, telemetry, functions, ct), equivalenceKey: Title),
            diagnostic);
    }

    private static Task<Document> MoveAsync(
        Document document, SyntaxNode root, InvocationExpressionSyntax telemetry, InvocationExpressionSyntax functions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var telemetryMember = (MemberAccessExpressionSyntax)telemetry.Expression;
        var functionsMember = (MemberAccessExpressionSyntax)functions.Expression;

        // Keep the chain's layout: the moved call gets the separator the tool-loop call had (newline + indent in a fluent
        // chain, nothing on one line), and whatever followed the tool-loop call now follows the moved call.
        SyntaxTriviaList separatorBefore = functionsMember.OperatorToken.GetPreviousToken().TrailingTrivia;

        SyntaxNode newRoot = root.ReplaceNodes(
            new[] { telemetry, functions },
            (original, rewritten) =>
            {
                if (original == telemetry)
                {
                    return telemetryMember.Expression.WithTrailingTrivia(telemetry.GetTrailingTrivia());
                }

                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        ((ExpressionSyntax)rewritten).WithTrailingTrivia(separatorBefore),
                        telemetryMember.OperatorToken.WithLeadingTrivia(functionsMember.OperatorToken.LeadingTrivia),
                        telemetryMember.Name),
                    telemetry.ArgumentList.WithTrailingTrivia(functions.GetTrailingTrivia()));
            });

        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }
}
