using System.Collections.Generic;
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
using Microsoft.CodeAnalysis.Simplification;

namespace XBag.AI.Analyzers;

/// <summary>
/// XBAG006: rewrites <c>response.Messages = expr;</c> into an in-place update of the existing list:
/// <code>
/// var newMessages = new List&lt;ChatMessage&gt;(expr);
/// response.Messages.Clear();
/// foreach (var message in newMessages) { response.Messages.Add(message); }
/// </code>
/// The copy is taken before <c>Clear()</c>, so <c>expr</c> may read (or even be) the current list. Offered for a plain
/// statement inside a block.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AgentResponseMessagesCodeFix)), Shared]
public sealed class AgentResponseMessagesCodeFix : CodeFixProvider
{
    private const string Title = "Update AgentResponse.Messages in place";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticIds.AgentResponseMessagesReplaced);

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc/>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        Diagnostic diagnostic = context.Diagnostics[0];
        if (root?.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is not AssignmentExpressionSyntax assignment
            || assignment.Parent is not ExpressionStatementSyntax { Parent: BlockSyntax } statement)
        {
            return;
        }

        context.RegisterCodeFix(
            CodeAction.Create(Title, ct => RewriteAsync(context.Document, root, assignment, statement, ct), equivalenceKey: Title),
            diagnostic);
    }

    private static async Task<Document> RewriteAsync(
        Document document, SyntaxNode root, AssignmentExpressionSyntax assignment, ExpressionStatementSyntax statement, CancellationToken cancellationToken)
    {
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var taken = new HashSet<string>(model?.LookupSymbols(statement.SpanStart).Select(s => s.Name) ?? Enumerable.Empty<string>());
        string list = Unique("newMessages", taken);
        string item = Unique("message", taken);

        // Laid out by hand with the file's own newline and indentation: the formatter normalizes newlines to the workspace
        // default (CRLF on Windows) and does not add line breaks between statements.
        var block = (BlockSyntax)statement.Parent!;
        string newline = statement.GetTrailingTrivia().FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia)).ToFullString();
        newline = newline.Length == 0 ? "\n" : newline;
        string indent = LastWhitespace(statement.GetLeadingTrivia());
        string braceIndent = LastWhitespace(block.OpenBraceToken.LeadingTrivia);
        string unit = indent.Length > braceIndent.Length && indent.StartsWith(braceIndent, System.StringComparison.Ordinal)
            ? indent.Substring(braceIndent.Length)
            : "    ";

        string target = assignment.Left.WithoutTrivia().ToString();
        StatementSyntax[] statements =
        [
            SyntaxFactory.ParseStatement(
                $"var {list} = new global::System.Collections.Generic.List<global::Microsoft.Extensions.AI.ChatMessage>({assignment.Right.WithoutTrivia()});"),
            SyntaxFactory.ParseStatement($"{target}.Clear();"),
            SyntaxFactory.ParseStatement(
                $"foreach (var {item} in {list}){newline}{indent}{{{newline}{indent}{unit}{target}.Add({item});{newline}{indent}}}"),
        ];

        int index = block.Statements.IndexOf(statement);
        SyntaxList<StatementSyntax> updated = block.Statements
            .RemoveAt(index)
            .InsertRange(index, statements.Select((s, i) => s
                .WithLeadingTrivia(i == 0 ? statement.GetLeadingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.Whitespace(indent)))
                .WithTrailingTrivia(i == statements.Length - 1 ? statement.GetTrailingTrivia() : SyntaxFactory.TriviaList(SyntaxFactory.EndOfLine(newline)))
                .WithAdditionalAnnotations(Simplifier.Annotation)));

        return document.WithSyntaxRoot(root.ReplaceNode(block, block.WithStatements(updated)));
    }

    private static string LastWhitespace(SyntaxTriviaList trivia) =>
        trivia.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToFullString();

    private static string Unique(string name, HashSet<string> taken)
    {
        string candidate = name;
        for (int i = 2; taken.Contains(candidate); i++)
        {
            candidate = name + i;
        }

        taken.Add(candidate);
        return candidate;
    }
}
