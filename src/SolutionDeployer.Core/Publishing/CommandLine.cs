using System.Text;

namespace SolutionDeployer.Core.Publishing;

/// <summary>Quote-aware splitting of a free-form argument string into discrete arguments.</summary>
public static class CommandLine
{
    /// <summary>
    /// Splits <paramref name="arguments"/> on whitespace, honouring single and double quotes (which
    /// are removed). Each result is passed to the process via <c>ArgumentList</c>, so the OS handles
    /// re-escaping. Returns an empty list for null/blank input.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var hasToken = false;

        foreach (var c in arguments)
        {
            if (c == '"' && !inSingle)
            {
                inDouble = !inDouble;
                hasToken = true;
            }
            else if (c == '\'' && !inDouble)
            {
                inSingle = !inSingle;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inSingle && !inDouble)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
            result.Add(current.ToString());

        return result;
    }
}
