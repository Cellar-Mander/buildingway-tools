using System.Text;

namespace Buildingway.Tools;

/// <summary>Edits to a Keep a Changelog CHANGELOG.md with a "## Unreleased" section.</summary>
internal static class Changelog
{
    private const string Unreleased = "## Unreleased";

    private static readonly string[] KindOrder = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"];

    private static bool IsVersionHeading(string line) => line.StartsWith("## ", StringComparison.Ordinal);

    private static string[] Lines(string text) => Lf(text).Split('\n');

    // A CRLF file (Windows, core.autocrlf, no .gitattributes) is edited as LF and written back as CRLF
    private static bool IsCrlf(string text) => text.Contains("\r\n", StringComparison.Ordinal);

    private static string Lf(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Restore(string edited, bool crlf) => crlf ? edited.Replace("\n", "\r\n", StringComparison.Ordinal) : edited;

    /// <summary>True when a non-blank line (an entry or a ### heading) is under Unreleased.</summary>
    public static bool HasUnreleasedEntries(string text)
    {
        var inUnreleased = false;
        foreach (var line in Lines(text))
        {
            if (IsVersionHeading(line))
            {
                inUnreleased = line == Unreleased;
            }
            else if (inUnreleased && !string.IsNullOrWhiteSpace(line))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>True when the section right after Unreleased is version's dated one.</summary>
    public static bool NextSectionIs(string text, string version)
    {
        var afterUnreleased = false;
        foreach (var line in Lines(text).Where(IsVersionHeading))
        {
            if (afterUnreleased)
            {
                return line.StartsWith($"## {version} - ", StringComparison.Ordinal);
            }
            afterUnreleased = line == Unreleased;
        }
        return false;
    }

    /// <summary>Puts heading right below "## Unreleased", so its entries become that version's.</summary>
    public static string DateUnreleased(string text, string heading)
    {
        var crlf = IsCrlf(text);
        var output = new StringBuilder();
        var lines = SplitKeepingFinalNewline(Lf(text), out var finalNewline);
        foreach (var line in lines)
        {
            output.Append(line).Append('\n');
            if (line == Unreleased)
            {
                output.Append('\n').Append(heading).Append('\n');
            }
        }
        return Restore(Finish(output, finalNewline), crlf);
    }

    /// <summary>
    /// Renames the section after Unreleased, unreleased version oldVersion's, to heading and merges
    /// the Unreleased entries into it, each under its ### heading: Keep a Changelog's headings in
    /// their order first, then any others as they came. Blank lines in both sections are dropped
    /// and rewritten.
    /// </summary>
    public static string Reversion(string text, string oldVersion, string heading)
    {
        var crlf = IsCrlf(text);
        var oldPrefix = $"## {oldVersion} - ";
        var output = new StringBuilder();
        var released = new Dictionary<string, List<string>>();
        var added = new Dictionary<string, List<string>>();
        var order = new List<string>();
        var state = "";
        var key = "";

        var lines = SplitKeepingFinalNewline(Lf(text), out var finalNewline);
        foreach (var line in lines)
        {
            if (IsVersionHeading(line))
            {
                if (state == "old")
                {
                    WriteMerged();
                    output.Append('\n');
                    state = "after";
                }
                else if (state == "unreleased" && line.StartsWith(oldPrefix, StringComparison.Ordinal))
                {
                    state = "old";
                    key = "";
                    continue;
                }
                else if (line == Unreleased)
                {
                    output.Append(line).Append("\n\n");
                    state = "unreleased";
                    key = "";
                    continue;
                }
            }
            if (state is "unreleased" or "old")
            {
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    key = line;
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    if (key != "" && !order.Contains(key))
                    {
                        order.Add(key);
                    }
                    var into = state == "old" ? released : added;
                    if (!into.TryGetValue(key, out var list))
                    {
                        into[key] = list = [];
                    }
                    list.Add(line);
                }
                continue;
            }
            output.Append(line).Append('\n');
        }
        if (state == "old")
        {
            WriteMerged();
        }
        return Restore(Finish(output, finalNewline), crlf);

        void WriteMerged()
        {
            output.Append(heading).Append('\n');
            WriteBlock("");
            var known = KindOrder.Select(k => "### " + k).Where(order.Contains).ToList();
            foreach (var k in known.Concat(order.Where(k => !known.Contains(k))))
            {
                WriteBlock(k);
            }
        }

        void WriteBlock(string blockKey)
        {
            var entries = released.GetValueOrDefault(blockKey, []).Concat(added.GetValueOrDefault(blockKey, [])).ToList();
            if (blockKey != "")
            {
                output.Append('\n').Append(blockKey).Append('\n');
            }
            if (entries.Count > 0)
            {
                output.Append('\n').Append(string.Join('\n', entries)).Append('\n');
            }
        }
    }

    /// <summary>The lines of text; finalNewline says whether it ended in one (most files do).</summary>
    private static string[] SplitKeepingFinalNewline(string text, out bool finalNewline)
    {
        finalNewline = text.EndsWith('\n');
        return (finalNewline ? text[..^1] : text).Split('\n');
    }

    private static string Finish(StringBuilder output, bool finalNewline)
    {
        if (!finalNewline && output.Length > 0 && output[^1] == '\n')
        {
            output.Length--;
        }
        return output.ToString();
    }
}
