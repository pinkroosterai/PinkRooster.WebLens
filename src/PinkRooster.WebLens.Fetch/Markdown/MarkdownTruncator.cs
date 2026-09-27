using System.Text;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Cuts Markdown to a length at a block boundary, never inside a fenced code block. When even the first
/// block is too long, it is cut at the last line (or, failing that, word) that fits; a fenced block cut that way is closed.
/// A listing's list that does not fit is cut between its items instead of being dropped whole: a listing is little more
/// than one long list. Articles are cut as before.
/// </summary>
internal static class MarkdownTruncator
{
    public static ConvertedMarkdown Truncate(string markdown, int maxChars, bool splitLists = false)
    {
        if (markdown.Length <= maxChars)
        {
            return new ConvertedMarkdown(markdown, false);
        }

        var output = new StringBuilder();
        foreach (var block in Blocks(markdown))
        {
            var separator = output.Length == 0 ? "" : "\n";
            if (output.Length + separator.Length + block.Length > maxChars)
            {
                if (output.Length == 0)
                {
                    output.Append(CutBlock(block, maxChars));
                }
                else if (splitLists && IsList(block))
                {
                    foreach (var item in block.TrimEnd('\n').Split('\n'))
                    {
                        if (output.Length + separator.Length + item.Length + 1 > maxChars)
                        {
                            break;
                        }

                        output.Append(separator).Append(item).Append('\n');
                        separator = "";
                    }
                }

                break;
            }

            output.Append(separator).Append(block);
        }

        return new ConvertedMarkdown(output.ToString().TrimEnd('\n') + "\n", true);
    }

    private static bool IsList(string block) => block.Length > 2 && block[0] is '-' or '*' or '+' && block[1] == ' ';

    /// <summary>Blocks separated by blank lines; a fenced code block is one block whatever blank lines it holds.</summary>
    private static List<string> Blocks(string markdown)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();
        string? fence = null;
        foreach (var line in markdown.Split('\n'))
        {
            if (fence is null && line.Length == 0)
            {
                if (current.Length > 0)
                {
                    blocks.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (MarkdownNormalizer.IsFence(line, out var marker))
            {
                if (fence is null)
                {
                    fence = marker;
                }
                else if (marker.StartsWith(fence, StringComparison.Ordinal))
                {
                    fence = null;
                }
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            blocks.Add(current.ToString());
        }

        return blocks;
    }

    private static string CutBlock(string block, int maxChars)
    {
        var lines = block.Split('\n');
        var fenced = MarkdownNormalizer.IsFence(lines[0], out var marker);
        var closing = fenced ? marker + "\n" : "";
        var budget = maxChars - closing.Length;

        var output = new StringBuilder();
        foreach (var line in lines)
        {
            if (output.Length + line.Length + 1 > budget)
            {
                break;
            }

            output.Append(line).Append('\n');
        }

        if (output.Length == 0 && !fenced && budget > 0)
        {
            // One enormous line: cut at the last space that fits.
            var cut = block[..budget];
            var space = cut.LastIndexOf(' ');
            output.Append(space > 0 ? cut[..space] : cut).Append('\n');
        }

        return output.Append(closing).ToString();
    }
}
