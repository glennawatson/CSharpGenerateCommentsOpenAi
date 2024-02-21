using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using Polly;
using Polly.Retry;

namespace CSharpGenerateCommentsOpenAi;

internal class CommentAddingRewriter(OpenAIClient client) : CSharpSyntaxRewriter
{
    private static readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions()
        {
            ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TaskCanceledException>(),
            MaxRetryAttempts = 10,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(.5),
        })
        .Build(); // Builds the resilience pipeline

    private readonly OpenAIClient _client = client;

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "class").Result;

        return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "method").Result;

        return base.VisitMethodDeclaration(node);
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "property").Result;

        return base.VisitPropertyDeclaration(node);
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "field").Result;

        return base.VisitFieldDeclaration(node);
    }

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "delegate").Result;

        return base.VisitDelegateDeclaration(node);
    }

    public override SyntaxNode? VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "enum").Result;

        return base.VisitEnumMemberDeclaration(node);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "class").Result;

        return base.VisitRecordDeclaration(node);
    }

    public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "class").Result;

        return base.VisitEnumDeclaration(node);
    }

    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        node = AddCommentToNode(node, "event").Result;

        return base.VisitEventFieldDeclaration(node);
    }

    private static List<SyntaxTrivia> GetXmlCommentTrivia(string commentTrivia, string indentation, SyntaxNode rootNode)
    {
        var syntaxTree = SyntaxFactory.ParseSyntaxTree(commentTrivia);
        var xmlComments = new List<SyntaxTrivia>();
        bool firstCommentNodeFound = false;

        foreach (var node in syntaxTree.GetRoot().DescendantNodesAndTokens())
        {
            if (firstCommentNodeFound)
            {
                break; // Stop if the first comment node has been processed
            }

            var leadingTrivia = node.GetLeadingTrivia();
            if (leadingTrivia.Any(trivia => trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                            trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
            {
                firstCommentNodeFound = true;

                foreach (var trivia in leadingTrivia)
                {
                    if (trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                    {
                        // Prepend indentation to each line of the comment
                        var commentWithIndentation = PrependIndentationToComment(trivia.ToString(), indentation, rootNode);

                        xmlComments.Add(SyntaxFactory.SyntaxTrivia(SyntaxKind.SingleLineCommentTrivia, commentWithIndentation));
                    }
                }
            }
        }

        return xmlComments;
    }

    private static int GetLeadingSpaces(SyntaxNode? node)
    {
        if (node is null)
        {
            return 0;
        }

        var tree = node.SyntaxTree;

        if (tree is null)
        {
            return 0;
        }

        var lineSpan = node.GetLocation().GetLineSpan();
        var startLine = tree.GetText().Lines[lineSpan.StartLinePosition.Line];

        var lineText = startLine.ToString();

        return lineText.TakeWhile(char.IsWhiteSpace).Count();
    }

    private static string PrependIndentationToComment(string comment, string indentation, SyntaxNode rootNode)
    {
        var lines = comment.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var indentedLines = lines.Select(line =>
        {
            // Removes carriage return if present
            // Add both indentation and '///' to each non-empty line
            line = line.TrimEnd('\r');

            return string.IsNullOrEmpty(line.Trim()) ? string.Empty : indentation + "/// " + line.TrimStart('/');
        });

        return string.Join(Environment.NewLine, indentedLines);
    }

    // Method to add comment to a node
    private async Task<T> AddCommentToNode<T>(T node, string nodeType)
        where T : SyntaxNode
    {
        var commentData = await QuerySurveyForResponse(nodeType, node);

        var indentation = new string(' ', GetLeadingSpaces(node));

        var xmlCommentTrivia = GetXmlCommentTrivia(commentData, indentation, node);
        if (xmlCommentTrivia.Count > 0)
        {
            // Filter out existing comment trivia and preserve other leading trivia
            var leadingTrivia = node.GetLeadingTrivia()
                                    .Where(trivia => !trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                                                     && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                                                     && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                                                     && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                                    .TakeWhile(trivia => !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                                    .ToSyntaxTriviaList();

            // Add a new line if there isn't one already
            if (leadingTrivia.Count == 0 || !leadingTrivia.Last().IsKind(SyntaxKind.EndOfLineTrivia))
            {
                leadingTrivia = leadingTrivia.Add(SyntaxFactory.CarriageReturnLineFeed);
            }

            // Add the new comment
            leadingTrivia = leadingTrivia.AddRange(xmlCommentTrivia);

            // Ensure there's a new line after the comment for proper spacing
            leadingTrivia = leadingTrivia
                .Add(SyntaxFactory.CarriageReturnLineFeed)
                .Add(SyntaxFactory.SyntaxTrivia(SyntaxKind.WhitespaceTrivia, indentation));

            return node.WithLeadingTrivia(leadingTrivia);
        }

        return node;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.ReadabilityRules", "SA1118:Parameter should not span multiple lines", Justification = "Deliberate")]
    private ValueTask<string> QuerySurveyForResponse(string memberType, SyntaxNode syntaxDeclaration)
    {
        var codeContentLines = syntaxDeclaration.ToFullString().Split(Environment.NewLine);
        var codeContent = string.Join(Environment.NewLine, codeContentLines.Select(x => x.Trim())).WithMaxLength(14000);

        string triviaInstructions = memberType switch
        {
            "class" => "Only provide a xml comment on the top class itself, not its methods/properties/fields.",
            "method" => "Provide two-three sentence remarks how the method works (without technical details like Linq operations etc). No exception information.",
            "field" => "Provide only a brief description about the field",
            "property" => "Provide only the summary section of the xml comment nothing else.",
            _ => "Provide only the xml summary section",
        };

        var messages = new Message[]
            {
                    new(
                        Role.System,
                        @$"You generate C# XML comments.
You only produce XML comments. Do not provide anything but the XML comment for easy copy/paste.
You do not generate any remarks sections.
{triviaInstructions}
You will use StyleCop formatting rules."),
                    new(Role.User, "Here is my " + memberType + " that I want you to generate XML comment for" + Environment.NewLine + codeContent),
            };

        var chatRequest = new ChatRequest(messages, Model.GPT3_5_Turbo_16K);

        return _pipeline.ExecuteAsync(async _ => await Task.Run<string>(async () =>
        {
            var response = await _client.ChatEndpoint.GetCompletionAsync(chatRequest);
            var commentData = response.FirstChoice.Message;
            return commentData;
        }));
    }
}
