using System.Text;

namespace RobotCommand.Cli;

/// <summary>Small shell-style tokenizer for the interactive CLI.</summary>
/// <remarks>
/// The terminal is not a host shell, so quoted names and notes must be split
/// here rather than relying on <see cref="string.Split(char, StringSplitOptions)"/>.
/// </remarks>
internal static class CliTokenizer
{
    public static string[] Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaping = false;
        foreach (var character in input)
        {
            if (escaping)
            {
                current.Append(character);
                escaping = false;
                continue;
            }
            if (character == '\\')
            {
                escaping = true;
                continue;
            }
            if (quote is not null)
            {
                if (character == quote.Value) quote = null;
                else current.Append(character);
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (escaping) current.Append('\\');
        if (quote is not null) throw new ArgumentException("An interactive CLI quote was not closed.");
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToArray();
    }
}
