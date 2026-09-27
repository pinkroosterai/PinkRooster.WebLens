using System.Text;

namespace PinkRooster.WebLens.Fetch;

/// <summary>
/// Markdown-aware clean-up: LF line endings; outside code, NBSP becomes a space and zero-width formatting
/// characters go; trailing whitespace goes; runs of blank lines become one. Fenced code and inline code are never touched.
/// No NFKC: it alters technical symbols. NFC only when configured.
/// </summary>
internal static class MarkdownNormalizer
{
    private static readonly char[] ZeroWidth = ['​', '‌', '‍', '⁠', '﻿'];

    public static string Normalize(string markdown, bool nfc)
    {
        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (nfc)
        {
            text = text.Normalize(NormalizationForm.FormC);
        }

        var output = new StringBuilder(text.Length);
        string? fence = null;
        var blankRun = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            if (fence is not null)
            {
                output.Append(rawLine).Append('\n');
                if (IsFence(rawLine, out var closing) && closing.StartsWith(fence, StringComparison.Ordinal) && rawLine.Trim().Length == closing.Length)
                {
                    fence = null;
                }

                continue;
            }

            if (IsFence(rawLine, out var opening))
            {
                fence = opening;
                blankRun = 0;
                output.Append(rawLine.TrimEnd()).Append('\n');
                continue;
            }

            var line = CleanOutsideInlineCode(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                if (++blankRun > 1 || output.Length == 0)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            output.Append(line).Append('\n');
        }

        return output.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>A line opening or closing a fenced code block: three or more backticks or tildes, up to three spaces in.</summary>
    internal static bool IsFence(string line, out string marker)
    {
        marker = "";
        var trimmed = line.TrimStart(' ');
        if (line.Length - trimmed.Length > 3 || trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return false;
        }

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == trimmed[0])
        {
            count++;
        }

        if (count < 3)
        {
            return false;
        }

        marker = trimmed[..count];
        return true;
    }

    private static string CleanOutsideInlineCode(string line)
    {
        var builder = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '`')
            {
                // An inline code span runs to the next backtick run of the same length; copy it verbatim.
                var run = 0;
                while (i + run < line.Length && line[i + run] == '`')
                {
                    run++;
                }

                var delimiter = new string('`', run);
                var close = line.IndexOf(delimiter, i + run, StringComparison.Ordinal);
                var end = close < 0 ? i + run : close + run;
                builder.Append(line, i, end - i);
                i = end;
                continue;
            }

            var c = line[i];
            if (c == ' ')
            {
                builder.Append(' ');
            }
            else if (Array.IndexOf(ZeroWidth, c) < 0)
            {
                builder.Append(c);
            }

            i++;
        }

        return builder.ToString();
    }
}
