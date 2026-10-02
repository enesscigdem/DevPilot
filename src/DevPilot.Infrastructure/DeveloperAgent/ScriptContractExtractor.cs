using System.Text;
using System.Text.RegularExpressions;

namespace DevPilot.Infrastructure.DeveloperAgent;

/// <summary>
/// Bounded TS/JS public-contract extraction. Preserves exported types and signatures
/// without method or function implementations.
/// </summary>
internal static class ScriptContractExtractor
{
    private static readonly Regex ExportTypeInterfaceEnum = new(
        @"^\s*export\s+(?:declare\s+)?(?:default\s+)?(?:abstract\s+)?(?:type|interface|enum)\b",
        RegexOptions.Compiled);

    private static readonly Regex ExportClass = new(
        @"^\s*export\s+(?:declare\s+)?(?:default\s+)?(?:abstract\s+)?class\b",
        RegexOptions.Compiled);

    private static readonly Regex ExportFunction = new(
        @"^\s*export\s+(?:default\s+)?(?:async\s+)?function\b",
        RegexOptions.Compiled);

    private static readonly Regex ExportBinding = new(
        @"^\s*export\s+(?:declare\s+)?(?:const|let|var)\b",
        RegexOptions.Compiled);

    private static readonly Regex ExportReexport = new(
        @"^\s*export\s+(?:type\s+)?\{",
        RegexOptions.Compiled);

    private static readonly Regex PrivateMember = new(
        @"^\s*(?:private|protected|#)",
        RegexOptions.Compiled);

    private static readonly Regex ClassMethod = new(
        @"^\s*(?:public\s+|static\s+|async\s+|readonly\s+|override\s+|abstract\s+|get\s+|set\s+)*constructor\s*(?:<[^<>]*>)?\s*\(|" +
        @"^\s*(?:public\s+|static\s+|async\s+|readonly\s+|override\s+|abstract\s+|get\s+|set\s+)*[A-Za-z_]\w*\s*(?:<[^<>]*>)?\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ClassProperty = new(
        @"^\s*(?:public\s+|static\s+|readonly\s+|declare\s+|override\s+|abstract\s+)*[A-Za-z_]\w*\s*(?:\?:|:|!?=)",
        RegexOptions.Compiled);

    public static string? Extract(string source, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');
        var selected = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith('*'))
            {
                i++;
                continue;
            }

            if (ExportClass.IsMatch(trimmed))
            {
                CaptureClassContract(lines, ref i, selected);
                continue;
            }

            if (ExportFunction.IsMatch(trimmed))
            {
                CaptureSignatureThenSkipBody(lines, ref i, selected);
                continue;
            }

            if (ExportTypeInterfaceEnum.IsMatch(trimmed) || ExportReexport.IsMatch(trimmed))
            {
                CaptureTypeLikeContract(lines, ref i, selected);
                continue;
            }

            if (ExportBinding.IsMatch(trimmed))
            {
                CaptureBindingSignature(lines, ref i, selected);
                continue;
            }

            i++;
        }

        if (selected.Count == 0)
        {
            return null;
        }

        var joined = string.Join('\n', selected);
        return joined.Length <= maxChars ? joined : joined[..maxChars];
    }

    private static void CaptureClassContract(string[] lines, ref int index, List<string> selected)
    {
        var header = new StringBuilder();
        var sawOpenBrace = false;
        while (index < lines.Length)
        {
            var line = lines[index];
            header.Append(line);
            if (line.Contains('{'))
            {
                sawOpenBrace = true;
                index++;
                break;
            }

            header.Append('\n');
            index++;
        }

        var headerText = header.ToString();
        var braceIndex = headerText.IndexOf('{');
        var classHeader = braceIndex >= 0 ? headerText[..braceIndex].TrimEnd() : headerText.TrimEnd();
        selected.Add(classHeader + " {");

        if (!sawOpenBrace)
        {
            selected.Add("}");
            return;
        }

        var sameLineAfterBrace = braceIndex >= 0 ? headerText[(braceIndex + 1)..] : string.Empty;
        if (!string.IsNullOrWhiteSpace(sameLineAfterBrace) && sameLineAfterBrace.Contains('}'))
        {
            CaptureInlineClassMembers(sameLineAfterBrace, selected);
            selected.Add("}");
            return;
        }

        while (index < lines.Length)
        {
            var line = lines[index];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('}') && !ClassMethod.IsMatch(trimmed) && !ClassProperty.IsMatch(trimmed))
            {
                index++;
                break;
            }

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
            {
                index++;
                continue;
            }

            if (PrivateMember.IsMatch(trimmed))
            {
                SkipMember(lines, ref index);
                continue;
            }

            if (ClassMethod.IsMatch(trimmed))
            {
                CaptureSignatureThenSkipBody(lines, ref index, selected);
                continue;
            }

            if (ClassProperty.IsMatch(trimmed))
            {
                CapturePropertySignature(lines, ref index, selected);
                continue;
            }

            index++;
        }

        selected.Add("}");
    }

    private static void CaptureInlineClassMembers(string interior, List<string> selected)
    {
        var members = interior.Split(';');
        foreach (var raw in members)
        {
            var member = raw.Trim().TrimEnd('}');
            if (string.IsNullOrWhiteSpace(member) || PrivateMember.IsMatch(member))
            {
                continue;
            }

            var signature = StripImplementation(member);
            if (!string.IsNullOrWhiteSpace(signature))
            {
                selected.Add("  " + signature.Trim());
            }
        }
    }

    private static void CaptureTypeLikeContract(string[] lines, ref int index, List<string> selected)
    {
        var depth = 0;
        var sawBrace = false;
        while (index < lines.Length)
        {
            var line = lines[index];
            selected.Add(line.TrimEnd());
            var opens = CountUnquoted(line, '{');
            var closes = CountUnquoted(line, '}');
            if (opens > 0)
            {
                sawBrace = true;
            }

            depth += opens - closes;
            index++;
            if (sawBrace)
            {
                if (depth <= 0)
                {
                    break;
                }

                continue;
            }

            if (line.TrimEnd().EndsWith(';'))
            {
                break;
            }
        }
    }

    private static void CaptureBindingSignature(string[] lines, ref int index, List<string> selected)
    {
        var current = lines[index].TrimEnd();
        if (LooksLikeFunctionValue(current))
        {
            CaptureSignatureThenSkipBody(lines, ref index, selected);
            return;
        }

        selected.Add(StripImplementation(current).TrimEnd());
        index++;
    }

    private static void CaptureSignatureThenSkipBody(string[] lines, ref int index, List<string> selected)
    {
        var signature = new StringBuilder();
        var parenDepth = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            parenDepth += CountUnquoted(line, '(') - CountUnquoted(line, ')');
            var trimmedEnd = line.TrimEnd();
            var implAt = IndexOfImplementationStart(trimmedEnd);
            if (implAt >= 0 && parenDepth <= 0)
            {
                signature.Append(trimmedEnd[..implAt].TrimEnd());
                selected.Add(signature.ToString().TrimEnd());
                if (trimmedEnd.Contains('{'))
                {
                    SkipBalancedFrom(lines, ref index, '{', '}');
                }
                else
                {
                    index++;
                }

                return;
            }

            signature.Append(trimmedEnd);
            if (parenDepth <= 0 && trimmedEnd.EndsWith(';'))
            {
                selected.Add(signature.ToString().TrimEnd());
                index++;
                return;
            }

            signature.Append('\n');
            index++;
        }

        if (signature.Length > 0)
        {
            selected.Add(signature.ToString().TrimEnd());
        }
    }

    private static void CapturePropertySignature(string[] lines, ref int index, List<string> selected)
    {
        var current = lines[index].TrimEnd();
        selected.Add(StripImplementation(current).TrimEnd());
        if (current.Contains('{'))
        {
            SkipBalancedFrom(lines, ref index, '{', '}');
            return;
        }

        index++;
    }

    private static void SkipMember(string[] lines, ref int index)
    {
        var line = lines[index];
        if (line.Contains('{'))
        {
            SkipBalancedFrom(lines, ref index, '{', '}');
            return;
        }

        index++;
    }

    private static void SkipBalancedFrom(string[] lines, ref int index, char open, char close)
    {
        var depth = 0;
        var started = false;
        while (index < lines.Length)
        {
            var line = lines[index];
            var opens = CountUnquoted(line, open);
            var closes = CountUnquoted(line, close);
            if (opens > 0)
            {
                started = true;
            }

            depth += opens - closes;
            index++;
            if (started && depth <= 0)
            {
                return;
            }
        }
    }

    private static string StripImplementation(string text)
    {
        var implAt = IndexOfImplementationStart(text);
        return implAt >= 0 ? text[..implAt].TrimEnd() : text.TrimEnd().TrimEnd(';');
    }

    private static int IndexOfImplementationStart(string text)
    {
        var arrow = text.IndexOf("=>", StringComparison.Ordinal);
        var brace = text.IndexOf('{');
        if (arrow < 0)
        {
            return brace;
        }

        if (brace < 0)
        {
            return arrow;
        }

        return Math.Min(arrow, brace);
    }

    private static bool LooksLikeFunctionValue(string line) =>
        line.Contains("=>") ||
        Regex.IsMatch(line, @"=\s*(?:async\s+)?(?:function\b|\()");

    private static int CountUnquoted(string line, char token)
    {
        var count = 0;
        var inSingle = false;
        var inDouble = false;
        var inTemplate = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '\\' && (inSingle || inDouble || inTemplate))
            {
                i++;
                continue;
            }

            if (ch == '\'' && !inDouble && !inTemplate)
            {
                inSingle = !inSingle;
                continue;
            }

            if (ch == '"' && !inSingle && !inTemplate)
            {
                inDouble = !inDouble;
                continue;
            }

            if (ch == '`' && !inSingle && !inDouble)
            {
                inTemplate = !inTemplate;
                continue;
            }

            if (!inSingle && !inDouble && !inTemplate && ch == token)
            {
                count++;
            }
        }

        return count;
    }
}
