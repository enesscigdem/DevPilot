using System.Text;
using System.Text.RegularExpressions;
using DevPilot.Domain.Enums;

namespace DevPilot.Application.Tasks.Batch;

public sealed record TaskDraft(
    string Title,
    string Description,
    string? AcceptanceCriteria,
    DevelopmentTaskPriority Priority);

/// <summary>A note about the pasted text; <see cref="Code"/> is stable so the UI can translate it.</summary>
public sealed record TaskBatchWarning(string Code, int? DraftIndex = null);

public sealed record TaskBatchParseResult(IReadOnlyList<TaskDraft> Drafts, IReadOnlyList<TaskBatchWarning> Warnings);

/// <summary>
/// Turns pasted text into task drafts without any AI: deterministic, instant and free, so the person always sees
/// exactly what will be created. Recognises "Task:/Description:" blocks (English or Turkish labels, optional bullets
/// or bold markers) and falls back to a bulleted or numbered list of titles.
/// </summary>
public static class TaskBatchParser
{
    public const int MaxTasks = 50;
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 10000;

    private enum Field { None, Title, Description, Acceptance, Priority }

    private static readonly Regex Label = new(
        @"^\s*(?:[-*•]\s*)?(?:\*\*)?(?<key>task|görev|gorev|title|başlık|baslik|description|açıklama|aciklama|acceptance criteria|kabul kriterleri|priority|öncelik|oncelik)(?:\*\*)?\s*[:：]\s*(?:\*\*)?(?<value>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ListItem = new(
        @"^(?<indent>\s*)(?:\d+[.)]|[-*•])\s+(?<text>\S.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TaskBatchParseResult Parse(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var badPriorities = new List<int>();
        var drafts = lines.Any(line => IsTitleLabel(line))
            ? ParseLabelled(lines, badPriorities, out var preamble)
            : ParseList(lines, out preamble);

        var warnings = new List<TaskBatchWarning>();
        if (preamble)
        {
            warnings.Add(new("TextBeforeFirstTask"));
        }

        if (drafts.Count == 0)
        {
            warnings.Add(new("NoTasksFound"));
            return new TaskBatchParseResult(Array.Empty<TaskDraft>(), warnings);
        }

        if (drafts.Count > MaxTasks)
        {
            warnings.Add(new("TooManyTasks"));
            drafts = drafts.Take(MaxTasks).ToList();
        }

        warnings.AddRange(badPriorities.Where(index => index < drafts.Count).Select(index => new TaskBatchWarning("UnknownPriority", index)));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            if (draft.Title.Length == 0)
            {
                warnings.Add(new("MissingTitle", i));
            }

            if (string.IsNullOrWhiteSpace(draft.Description))
            {
                warnings.Add(new("MissingDescription", i));
            }

            if (draft.Title.Length > MaxTitleLength)
            {
                warnings.Add(new("TitleTooLong", i));
            }

            if (draft.Description.Length > MaxDescriptionLength)
            {
                warnings.Add(new("DescriptionTooLong", i));
            }

            if (!seen.Add(draft.Title))
            {
                warnings.Add(new("DuplicateTitle", i));
            }
        }

        return new TaskBatchParseResult(drafts, warnings);
    }

    private static bool IsTitleLabel(string line)
    {
        var match = Label.Match(line);
        return match.Success && KeyToField(match.Groups["key"].Value) == Field.Title;
    }

    private static Field KeyToField(string key) => key.ToLowerInvariant() switch
    {
        "task" or "görev" or "gorev" or "title" or "başlık" or "baslik" => Field.Title,
        "description" or "açıklama" or "aciklama" => Field.Description,
        "acceptance criteria" or "kabul kriterleri" => Field.Acceptance,
        _ => Field.Priority
    };

    private sealed class Builder
    {
        public readonly StringBuilder Title = new();
        public readonly StringBuilder Description = new();
        public readonly StringBuilder Acceptance = new();
        public string Priority = string.Empty;

        public TaskDraft Build(List<int> priorityIssues, int index)
        {
            var priority = ParsePriority(Priority, out var recognised);
            if (!recognised)
            {
                priorityIssues.Add(index);
            }

            var acceptance = Acceptance.ToString().Trim();
            return new TaskDraft(
                Title.ToString().Trim(),
                Description.ToString().Trim(),
                acceptance.Length == 0 ? null : acceptance,
                priority);
        }
    }

    private static List<TaskDraft> ParseLabelled(string[] lines, List<int> badPriorities, out bool preamble)
    {
        preamble = false;
        var builders = new List<Builder>();
        Builder? current = null;
        var field = Field.None;

        foreach (var line in lines)
        {
            var match = Label.Match(line);
            if (match.Success)
            {
                var next = KeyToField(match.Groups["key"].Value);
                var value = match.Groups["value"].Value.Trim().TrimEnd('*').Trim();
                if (next == Field.Title)
                {
                    current = new Builder();
                    builders.Add(current);
                }

                if (current is null)
                {
                    // A "Description:" before any "Task:" belongs to nothing.
                    preamble = true;
                    continue;
                }

                field = next;
                Append(current, field, value);
                continue;
            }

            if (current is null)
            {
                preamble |= !string.IsNullOrWhiteSpace(line);
                continue;
            }

            // A title never spans lines: extra text after "Task:" is the start of the description.
            if (field == Field.Title && !string.IsNullOrWhiteSpace(line))
            {
                field = Field.Description;
            }

            if (field != Field.None)
            {
                Append(current, field, line.TrimEnd());
            }
        }

        // Drafts that are completely empty are dropped before indexes are assigned, so warnings point at what the person sees.
        var drafts = new List<TaskDraft>();
        foreach (var builder in builders)
        {
            var draft = builder.Build(badPriorities, drafts.Count);
            if (draft.Title.Length > 0 || draft.Description.Length > 0)
            {
                drafts.Add(draft);
            }
            else
            {
                badPriorities.RemoveAll(index => index == drafts.Count);
            }
        }

        return drafts;
    }

    private static void Append(Builder builder, Field field, string text)
    {
        var target = field switch
        {
            Field.Title => builder.Title,
            Field.Description => builder.Description,
            Field.Acceptance => builder.Acceptance,
            _ => null
        };

        if (field == Field.Priority)
        {
            builder.Priority = (builder.Priority + " " + text).Trim();
            return;
        }

        if (target is null)
        {
            return;
        }

        if (target.Length > 0)
        {
            target.Append('\n');
        }

        target.Append(text);
    }

    private static List<TaskDraft> ParseList(string[] lines, out bool preamble)
    {
        preamble = false;
        var drafts = new List<(StringBuilder Title, StringBuilder Description)>();
        var firstIndent = -1;

        foreach (var line in lines)
        {
            var item = ListItem.Match(line);
            var indent = item.Success ? item.Groups["indent"].Value.Length : -1;
            if (item.Success && (firstIndent < 0 || indent <= firstIndent))
            {
                firstIndent = firstIndent < 0 ? indent : Math.Min(firstIndent, indent);
                drafts.Add((new StringBuilder(item.Groups["text"].Value.Trim()), new StringBuilder()));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (drafts.Count == 0)
            {
                preamble = true;
                continue;
            }

            var description = drafts[^1].Description;
            if (description.Length > 0)
            {
                description.Append('\n');
            }

            description.Append(line.Trim());
        }

        return drafts
            .Select(d => new TaskDraft(d.Title.ToString().Trim(), d.Description.ToString().Trim(), null, DevelopmentTaskPriority.Medium))
            .ToList();
    }

    public static DevelopmentTaskPriority ParsePriority(string? text, out bool recognised)
    {
        recognised = true;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "":
            case "medium" or "normal" or "orta":
                return DevelopmentTaskPriority.Medium;
            case "low" or "düşük" or "dusuk":
                return DevelopmentTaskPriority.Low;
            case "high" or "yüksek" or "yuksek":
                return DevelopmentTaskPriority.High;
            case "critical" or "urgent" or "kritik" or "acil":
                return DevelopmentTaskPriority.Critical;
            default:
                recognised = false;
                return DevelopmentTaskPriority.Medium;
        }
    }
}
