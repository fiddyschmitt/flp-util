using System.Globalization;

namespace FlpUtil.Cli;

/// <summary>
/// Minimal verb/option parser. Leading bare words are verbs ("index dump"); everything after the
/// first <c>--option</c> is an option, written either <c>--key value</c>, <c>--key=value</c> or as
/// a bare <c>--flag</c>.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, string?> _options;

    private CommandLine(List<string> verbs, Dictionary<string, string?> options)
    {
        Verbs = verbs;
        _options = options;
    }

    public IReadOnlyList<string> Verbs { get; }

    public static CommandLine Parse(string[] args)
    {
        var verbs = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        int i = 0;
        for (; i < args.Length && !args[i].StartsWith('-'); i++)
            verbs.Add(args[i]);

        for (; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith('-'))
                throw new CommandLineException($"Unexpected argument '{token}' (options must come after verbs).");

            string key = token.TrimStart('-');
            string? value = null;

            int eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                value = args[++i];
            }

            if (key.Length == 0)
                throw new CommandLineException($"Malformed option '{token}'.");

            options[key] = value;
        }

        return new CommandLine(verbs, options);
    }

    public bool HasVerb(params string[] verbs) =>
        verbs.Length <= Verbs.Count &&
        verbs.Select((v, i) => string.Equals(v, Verbs[i], StringComparison.OrdinalIgnoreCase)).All(match => match);

    public bool HasFlag(string name) => _options.ContainsKey(name);

    public string? GetString(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public string GetRequiredString(string name) =>
        GetString(name) ?? throw new CommandLineException($"Missing required option --{name}.");

    public int? GetInt(string name)
    {
        string? raw = GetString(name);
        if (raw is null)
            return null;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new CommandLineException($"Option --{name} expects a whole number, got '{raw}'.");
        return value;
    }

    /// <summary>Single character used as the CSV delimiter; accepts the word "tab".</summary>
    public char GetDelimiter(string name, char fallback)
    {
        string? raw = GetString(name);
        if (string.IsNullOrEmpty(raw))
            return fallback;
        if (raw.Equals("tab", StringComparison.OrdinalIgnoreCase) || raw == "\\t")
            return '\t';
        if (raw.Length != 1)
            throw new CommandLineException($"Option --{name} expects a single character or 'tab', got '{raw}'.");
        return raw[0];
    }
}

public sealed class CommandLineException(string message) : Exception(message);
