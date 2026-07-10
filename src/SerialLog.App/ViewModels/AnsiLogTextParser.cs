using System.Text;

namespace SerialLog.App.ViewModels;

public static class AnsiLogTextParser
{
    private const char Escape = '\u001b';

    public static IReadOnlyList<LogTextSegmentViewModel> Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var segments = new List<LogTextSegmentViewModel>();
        var buffer = new StringBuilder();
        string? foreground = null;
        var index = 0;

        while (index < text.Length)
        {
            if (TryReadSgr(text, index, out var nextIndex, out var codes))
            {
                Flush();
                foreground = ApplyCodes(foreground, codes);
                index = nextIndex;
                continue;
            }

            if (TryReadEscapeSequence(text, index, out nextIndex))
            {
                Flush();
                index = nextIndex;
                continue;
            }

            buffer.Append(text[index]);
            index++;
        }

        Flush();
        return segments;

        void Flush()
        {
            if (buffer.Length == 0)
            {
                return;
            }

            segments.Add(new LogTextSegmentViewModel(buffer.ToString(), foreground));
            buffer.Clear();
        }
    }

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(Escape, StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (TryReadSgr(text, index, out var nextIndex, out _))
            {
                index = nextIndex;
                continue;
            }

            if (TryReadEscapeSequence(text, index, out nextIndex))
            {
                index = nextIndex;
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool TryReadSgr(string text, int startIndex, out int nextIndex, out IReadOnlyList<int> codes)
    {
        nextIndex = startIndex;
        codes = [];

        if (startIndex + 2 >= text.Length || text[startIndex] != Escape || text[startIndex + 1] != '[')
        {
            return false;
        }

        var endIndex = startIndex + 2;
        while (endIndex < text.Length && text[endIndex] != 'm')
        {
            var ch = text[endIndex];
            if (!char.IsDigit(ch) && ch != ';')
            {
                return false;
            }

            endIndex++;
        }

        if (endIndex >= text.Length)
        {
            return false;
        }

        var parameterText = text[(startIndex + 2)..endIndex];
        codes = string.IsNullOrEmpty(parameterText)
            ? [0]
            : parameterText.Split(';').Select(part => int.TryParse(part, out var code) ? code : 0).ToArray();
        nextIndex = endIndex + 1;
        return true;
    }

    private static bool TryReadEscapeSequence(string text, int startIndex, out int nextIndex)
    {
        nextIndex = startIndex;
        if (startIndex + 1 >= text.Length || text[startIndex] != Escape)
        {
            return false;
        }

        if (text[startIndex + 1] == '[')
        {
            for (var index = startIndex + 2; index < text.Length; index++)
            {
                var ch = text[index];
                if (ch is >= '\u0040' and <= '\u007E')
                {
                    nextIndex = index + 1;
                    return true;
                }
            }

            return false;
        }

        if (text[startIndex + 1] == ']')
        {
            for (var index = startIndex + 2; index < text.Length; index++)
            {
                if (text[index] == '\u0007')
                {
                    nextIndex = index + 1;
                    return true;
                }

                if (text[index] == Escape && index + 1 < text.Length && text[index + 1] == '\\')
                {
                    nextIndex = index + 2;
                    return true;
                }
            }

            return false;
        }

        nextIndex = Math.Min(startIndex + 2, text.Length);
        return true;
    }

    private static string? ApplyCodes(string? currentForeground, IReadOnlyList<int> codes)
    {
        var foreground = currentForeground;
        for (var index = 0; index < codes.Count; index++)
        {
            var code = codes[index];
            if (code == 0 || code == 39)
            {
                foreground = null;
                continue;
            }

            if (code == 38 && TryReadExtendedColor(codes, ref index, out var extendedColor))
            {
                foreground = extendedColor;
                continue;
            }

            var mapped = MapAnsiForeground(code);
            if (mapped is not null)
            {
                foreground = mapped;
            }
        }

        return foreground;
    }

    private static bool TryReadExtendedColor(IReadOnlyList<int> codes, ref int index, out string? color)
    {
        color = null;
        if (index + 1 >= codes.Count)
        {
            return false;
        }

        var mode = codes[index + 1];
        if (mode == 2 && index + 4 < codes.Count)
        {
            var red = ClampColor(codes[index + 2]);
            var green = ClampColor(codes[index + 3]);
            var blue = ClampColor(codes[index + 4]);
            color = $"#{red:X2}{green:X2}{blue:X2}";
            index += 4;
            return true;
        }

        if (mode == 5 && index + 2 < codes.Count)
        {
            color = MapXtermColor(ClampColorIndex(codes[index + 2]));
            index += 2;
            return true;
        }

        return false;
    }

    private static int ClampColor(int value)
    {
        return Math.Clamp(value, 0, 255);
    }

    private static int ClampColorIndex(int value)
    {
        return Math.Clamp(value, 0, 255);
    }

    private static string? MapAnsiForeground(int code)
    {
        return code switch
        {
            30 => "#111827",
            31 => "#DC2626",
            32 => "#16A34A",
            33 => "#D97706",
            34 => "#2563EB",
            35 => "#9333EA",
            36 => "#0891B2",
            37 => "#4B5563",
            90 => "#6B7280",
            91 => "#EF4444",
            92 => "#22C55E",
            93 => "#F59E0B",
            94 => "#3B82F6",
            95 => "#A855F7",
            96 => "#06B6D4",
            97 => "#374151",
            _ => null
        };
    }

    private static string MapXtermColor(int index)
    {
        if (index < 16)
        {
            return index switch
            {
                0 => "#111827",
                1 => "#DC2626",
                2 => "#16A34A",
                3 => "#D97706",
                4 => "#2563EB",
                5 => "#9333EA",
                6 => "#0891B2",
                7 => "#4B5563",
                8 => "#6B7280",
                9 => "#EF4444",
                10 => "#22C55E",
                11 => "#F59E0B",
                12 => "#3B82F6",
                13 => "#A855F7",
                14 => "#06B6D4",
                _ => "#374151"
            };
        }

        if (index >= 232)
        {
            var level = 8 + (index - 232) * 10;
            return $"#{level:X2}{level:X2}{level:X2}";
        }

        var colorIndex = index - 16;
        var red = ColorCubeValue(colorIndex / 36);
        var green = ColorCubeValue(colorIndex / 6 % 6);
        var blue = ColorCubeValue(colorIndex % 6);
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    private static int ColorCubeValue(int value)
    {
        return value == 0 ? 0 : 55 + value * 40;
    }
}
