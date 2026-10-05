using System.Text;
using DevPilot.Domain.ProjectBrain.Entities;

namespace DevPilot.Application.ProjectBrain.Commands.AskBrain;

/// <summary>
/// Builds the pieces of a Project Brain request that depend on the conversation: the prompt that defines what the
/// assistant is allowed to do, the history block, and the retrieval query for short follow-up questions.
/// </summary>
public static class BrainPromptBuilder
{
    public const int MaxHistoryMessages = 8;
    private const int MaxUserHistoryChars = 600;
    private const int MaxAssistantHistoryChars = 900;
    private const int FollowUpWordThreshold = 10;
    private const int MaxPreviousQuestionChars = 240;

    public static string BuildSystemPrompt() =>
        @"You are Project Brain, a READ-ONLY code intelligence assistant inside DevPilot. You answer questions about one indexed repository using only the code excerpts you are given.

What you are NOT:
- You cannot change code, create files, run commands, open pull requests or create tasks. DevPilot's Tasks and Executions features do that, not you.
- Never offer to do those things (for example ""shall I implement it?"" or ""I can make that change""). If the user wants something built, say that DevPilot's Tasks page does that, and offer to help them write the task.

What you do:
1. Answer from the provided excerpts. Do not invent APIs, methods, file paths or line numbers that are not shown.
2. If the excerpts do not contain enough information, say briefly what is known and what is not. Do not pad the answer with generic lists of what you would need.
3. Use the conversation so far. Resolve words like ""it"", ""that file"" or ""the same"" from earlier turns instead of asking the user to repeat themselves.
4. If the user asks for a task title and description (for a feature, a fix or a change), write them: a short imperative title and a description with context, the affected files or components you can see in the excerpts, and acceptance criteria. This is a drafting request, not a question about the code, so the excerpts only need to ground the affected areas. Mark anything you could not confirm from the excerpts as an assumption.
5. Answer in the language of the user's latest message.
6. At the very end of your response, on a new line, list the exact source IDs you directly used, in the format:
SOURCES: [Source 1], [Source 2]
(If no sources were used, write SOURCES: None)";

    /// <summary>
    /// Renders the most recent turns before the current question. Long messages are shortened, failed turns are
    /// dropped, and the whole block is bounded so a long chat cannot crowd out the code excerpts.
    /// </summary>
    public static string BuildHistoryBlock(IEnumerable<ProjectBrainMessage> priorMessages)
    {
        var turns = SelectHistory(priorMessages);
        if (turns.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Conversation so far (oldest first; for context only, the code excerpts below remain the source of truth):");
        foreach (var message in turns)
        {
            var isUser = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase);
            sb.AppendLine($"{(isUser ? "User" : "Assistant")}: {Shorten(message.Content, isUser ? MaxUserHistoryChars : MaxAssistantHistoryChars)}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// A short follow-up ("do it for the other one", "bunu task yap") says nothing the index can match, so the
    /// previous user question is added to the retrieval query. Self-contained questions are used unchanged.
    /// </summary>
    public static string BuildSearchQuery(string question, IEnumerable<ProjectBrainMessage> priorMessages)
    {
        var current = question.Trim();
        if (WordCount(current) > FollowUpWordThreshold)
        {
            return current;
        }

        var previousQuestion = SelectHistory(priorMessages)
            .LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        return previousQuestion == null
            ? current
            : $"{Shorten(previousQuestion.Content, MaxPreviousQuestionChars)} {current}";
    }

    private static List<ProjectBrainMessage> SelectHistory(IEnumerable<ProjectBrainMessage> priorMessages) =>
        priorMessages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) &&
                        !m.Content.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.CreatedAt)
            .TakeLast(MaxHistoryMessages)
            .ToList();

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Shorten(string text, int maxChars)
    {
        var flat = text.Trim();
        return flat.Length <= maxChars ? flat : flat[..maxChars] + " …";
    }
}
