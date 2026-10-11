using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG004: inserts <c>options = options?.Clone();</c> as the first statement of the method or delegate that owns the
/// parameter (<c>options.Clone()</c> when the parameter is declared non-nullable). A null stays null, so the change is
/// behaviour-preserving except that the caller's instance is no longer written to. Block bodies only.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ChatOptionsMutationCodeFix)), Shared]
public sealed class ChatOptionsMutationCodeFix : CodeFixProvider
{
    private const string Title = "Clone the ChatOptions before changing it";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.CallerChatOptionsMutated);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        Diagnostic diagnostic = context.Diagnostics[0];
        if (root is null || model is null || !diagnostic.Properties.TryGetValue(ChatOptionsMutationAnalyzer.ParameterProperty, out string? name) || name is null)
        {
            return;
        }

        SyntaxNode write = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
        foreach (SyntaxNode owner in write.Ancestors())
        {
            (ParameterListSyntax? list, ParameterSyntax? single, BlockSyntax? body) = owner switch
            {
                MethodDeclarationSyntax m => (m.ParameterList, null, m.Body),
                LocalFunctionStatementSyntax f => (f.ParameterList, null, f.Body),
                ParenthesizedLambdaExpressionSyntax l => (l.ParameterList, null, l.Block),
                SimpleLambdaExpressionSyntax l => (null, l.Parameter, l.Block),
                AnonymousMethodExpressionSyntax a => (a.ParameterList, null, a.Block),
                _ => default((ParameterListSyntax?, ParameterSyntax?, BlockSyntax?)),
            };

            ParameterSyntax? parameter = single?.Identifier.ValueText == name
                ? single
                : list?.Parameters.FirstOrDefault(p => p.Identifier.ValueText == name);
            if (parameter is null)
            {
                continue;
            }

            if (body is null || model.GetDeclaredSymbol(parameter, context.CancellationToken) is not { } symbol)
            {
                return;
            }

            // Same indentation and newline as the statement it goes above (the formatter does not add line breaks).
            string access = symbol.NullableAnnotation == NullableAnnotation.NotAnnotated ? "." : "?.";
            SyntaxToken anchor = body.Statements.Count > 0 ? body.Statements[0].GetFirstToken() : body.OpenBraceToken.GetNextToken();
            SyntaxTrivia newline = body.OpenBraceToken.TrailingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            StatementSyntax clone = SyntaxFactory.ParseStatement($"{name} = {name}{access}Clone();")
                .WithLeadingTrivia(anchor.LeadingTrivia.Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)))
                .WithTrailingTrivia(newline.IsKind(SyntaxKind.EndOfLineTrivia) ? newline : SyntaxFactory.ElasticCarriageReturnLineFeed)
                .WithAdditionalAnnotations(Formatter.Annotation);
            context.RegisterCodeFix(
                CodeAction.Create(
                    Title,
                    ct => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(body, body.WithStatements(body.Statements.Insert(0, clone))))),
                    equivalenceKey: Title),
                diagnostic);
            return;
        }
    }
}
