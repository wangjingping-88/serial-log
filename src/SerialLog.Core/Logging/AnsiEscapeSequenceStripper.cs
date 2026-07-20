using System.Text;

namespace SerialLog.Core.Logging;

public static class AnsiEscapeSequenceStripper
{
    private const char Escape = '\u001b';
    private const char Csi = '\u009b';
    private const char Osc = '\u009d';
    private const char StringTerminator = '\u009c';

    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text) ||
            (text.IndexOf(Escape, StringComparison.Ordinal) < 0 &&
             text.IndexOf(Csi, StringComparison.Ordinal) < 0 &&
             text.IndexOf(Osc, StringComparison.Ordinal) < 0))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (TrySkipControlSequence(text, ref index))
            {
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool TrySkipControlSequence(string text, ref int index)
    {
        if (text[index] == Csi)
        {
            index = SkipCsi(text, index + 1);
            return true;
        }

        if (text[index] == Osc)
        {
            index = SkipOsc(text, index + 1);
            return true;
        }

        if (text[index] != Escape || index + 1 >= text.Length)
        {
            return false;
        }

        index++;
        if (text[index] == '[')
        {
            index = SkipCsi(text, index + 1);
            return true;
        }

        if (text[index] == ']')
        {
            index = SkipOsc(text, index + 1);
            return true;
        }

        index++;
        return true;
    }

    private static int SkipCsi(string text, int index)
    {
        while (index < text.Length)
        {
            if (text[index] is >= '\u0040' and <= '\u007e')
            {
                return index + 1;
            }

            index++;
        }

        return index;
    }

    private static int SkipOsc(string text, int index)
    {
        while (index < text.Length)
        {
            if (text[index] == '\a' || text[index] == StringTerminator)
            {
                return index + 1;
            }

            if (text[index] == Escape && index + 1 < text.Length && text[index + 1] == '\\')
            {
                return index + 2;
            }

            index++;
        }

        return index;
    }
}
