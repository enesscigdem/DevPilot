using System.Text.Json;
using DevPilot.Infrastructure.DeveloperAgent;
using FluentAssertions;
using Xunit;

namespace DevPilot.Tests;

public sealed class JsonStringRepairTests
{
    [Fact]
    public void Escapes_raw_newlines_and_inner_quotes()
    {
        var broken = "{\"filePath\":\"a.tsx\",\"newContent\":\"const a = \"x\";\nreturn <div className=\"b\" />;\"}";

        var repaired = DeveloperAgent.RepairJsonStringLiterals(broken);

        using var doc = JsonDocument.Parse(repaired);
        doc.RootElement.GetProperty("newContent").GetString()
            .Should().Be("const a = \"x\";\nreturn <div className=\"b\" />;");
    }

    [Fact]
    public void Quotes_followed_by_code_punctuation_stay_inside_the_string()
    {
        // cn("a", "b") and an object literal contain "," ":" and "}" right after a quote.
        var code = "const c = cn(\"a\", \"b\")\nconst o = { k: \"v\", z: [\"x\"] }\n<div style={{ color: \"red\" }} />";
        var broken = "{\"filePath\":\"a.tsx\",\"action\":\"Modify\",\"newContent\":\"" + code + "\"}";

        var repaired = DeveloperAgent.RepairJsonStringLiterals(broken);

        using var doc = JsonDocument.Parse(repaired);
        doc.RootElement.GetProperty("newContent").GetString().Should().Be(code);
        doc.RootElement.GetProperty("action").GetString().Should().Be("Modify");
    }

    [Fact]
    public void Search_replace_edits_with_unescaped_code_quotes_keep_their_structure()
    {
        var broken =
            "{\"filePath\":\"a.tsx\",\"action\":\"Modify\",\"searchReplaceEdits\":[" +
            "{\"search\":\"className={cn(\"a\", \"b\")}\",\"replace\":\"className={cn(\"c\", { d: \"e\" })}\"}," +
            "{\"search\":\"<p>x</p>\",\"replace\":\"<p>y</p>\"}]}";

        var repaired = DeveloperAgent.RepairJsonStringLiterals(broken);

        using var doc = JsonDocument.Parse(repaired);
        var edits = doc.RootElement.GetProperty("searchReplaceEdits");
        edits.GetArrayLength().Should().Be(2);
        edits[0].GetProperty("search").GetString().Should().Be("className={cn(\"a\", \"b\")}");
        edits[0].GetProperty("replace").GetString().Should().Be("className={cn(\"c\", { d: \"e\" })}");
        edits[1].GetProperty("replace").GetString().Should().Be("<p>y</p>");
    }

    [Fact]
    public void Invalid_regex_escapes_become_literal_backslashes()
    {
        var broken = "{\"newContent\":\"const r = /\\d+\\.\\d/;\"}";

        var repaired = DeveloperAgent.RepairJsonStringLiterals(broken);

        using var doc = JsonDocument.Parse(repaired);
        doc.RootElement.GetProperty("newContent").GetString().Should().Be("const r = /\\d+\\.\\d/;");
    }

    [Fact]
    public void Valid_json_is_unchanged()
    {
        var valid = "{\"a\":\"line" + "\\" + "n" + "\\" + "\"q" + "\\" + "\"\",\"b\":[1,2]}";

        DeveloperAgent.RepairJsonStringLiterals(valid).Should().Be(valid);
    }
}
