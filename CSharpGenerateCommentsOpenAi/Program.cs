using System.CommandLine;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.FileSystemGlobbing;
using OpenAI;

namespace CSharpGenerateCommentsOpenAi;

internal static class Program
{
    private static readonly object _lockConsoleObject = new();

    public static Task Main(string[] args)
    {
        MSBuildLocator.RegisterDefaults();

        var rootCommand = new RootCommand("Generate AI Comments");
        var apiKeyOption = new Option<string>(name: "--api-key", description: "The open AI key");
        var folderOption = new Option<string>("--folder", "The folder path to scan");
        var slnFileMatcher = new Option<string>("--sln-file-glob", () => "**/*.sln", "The glob to match sln, default all sln recursively");

        rootCommand.Add(apiKeyOption);
        rootCommand.Add(folderOption);
        rootCommand.Add(slnFileMatcher);

        rootCommand.SetHandler(
            (apiKeyOptionValue, folderOptionValue, slnFileMatcherValue) => ProcessChatAI(folderOptionValue, apiKeyOptionValue, slnFileMatcherValue),
            apiKeyOption,
            folderOption,
            slnFileMatcher);

        return rootCommand.InvokeAsync(args);
    }

    private static async Task ProcessChatAI(string? folder, string? apiKey, string? slnFileGlob)
    {
        var openAiClient = new OpenAIClient(apiKey);
        var rewriter = new CommentAddingRewriter(openAiClient);

        ArgumentException.ThrowIfNullOrWhiteSpace(folder, nameof(folder));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey, nameof(apiKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(slnFileGlob, nameof(slnFileGlob));

        Matcher matcher = new();
        matcher.AddInclude(slnFileGlob);

        var matchingFiles = matcher.GetResultsInFullPath(folder);

        using var workspace = MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = true;
        workspace.WorkspaceFailed += (o, e) =>
        {
            WriteError(e.Diagnostic.ToString());
        };

        foreach (var filePath in matchingFiles)
        {
            var solution = await workspace.OpenSolutionAsync(filePath);

            if (!solution.Projects.Any())
            {
                WriteError("No projects");
                return;
            }

            WriteInfo("Processing solution: " + filePath);

            await Parallel.ForEachAsync(solution.Projects.Where(x => x.Language == LanguageNames.CSharp), async (project, token) =>
            {
                foreach (var document in project.Documents
                    .Where(document =>
                        document.FilePath?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true &&
                        !document.FilePath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) &&
                        !document.FilePath.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var syntaxTree = await document.GetSyntaxTreeAsync();

                        if (syntaxTree is null)
                        {
                            return;
                        }

                        var rootNode = await syntaxTree.GetRootAsync();

                        var rewriter = new CommentAddingRewriter(openAiClient);
                        var newRoot = rewriter.Visit(rootNode);
                        var newDocument = document.WithSyntaxRoot(newRoot);

                        var newText = newRoot.ToFullString();
                        await File.WriteAllTextAsync(document.FilePath!, newText.ToString());

                        WriteInfo("Done writing " + document.FilePath);
                    }
                    catch (Exception ex)
                    {
                        WriteError("Could not process " + document.FilePath + " on exception " + ex);
                    }
                }
            });
        }
    }

    private static void WriteInfo(string message)
    {
        Task.Run(() =>
        {
            lock (_lockConsoleObject)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("[INFO] ");
                Console.ResetColor();
                Console.Write($"{message}");
                Console.WriteLine();
            }
        });
    }

    private static void WriteError(string message)
    {
        Task.Run(() =>
        {
            lock (_lockConsoleObject)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[ERROR] ");
                Console.ResetColor();
                Console.Write($"{message}");
                Console.WriteLine();
            }
        });
    }
}
