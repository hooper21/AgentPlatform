using System.Text.RegularExpressions;

namespace AgentPlatform.Knowledge.Ingest.Providers;

/// <summary>
/// Минимальный парсер robots.txt (MVP): группы User-agent с правилами
/// Allow/Disallow, приоритет самого длинного совпадения, поддержка '*' и '$'.
/// Соблюдение robots.txt обязательно при обходе site.
/// </summary>
internal sealed partial class RobotsRules
{
    private readonly List<(bool Allow, string Pattern)> _rules;

    private RobotsRules(List<(bool Allow, string Pattern)> rules) => _rules = rules;

    /// <summary>Пустой robots.txt — обход разрешён.</summary>
    public static RobotsRules AllowAll { get; } = new([]);

    /// <summary>Разбирает robots.txt и выбирает правила для указанного User-Agent.</summary>
    public static RobotsRules Parse(string? content, string userAgent)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return AllowAll;
        }

        var groups = new List<(List<string> Agents, List<(bool Allow, string Pattern)> Rules)>();
        List<string>? agents = null;
        var rules = new List<(bool Allow, string Pattern)>();
        var inRules = false;

        void Flush()
        {
            if (agents is { Count: > 0 })
            {
                groups.Add((agents, rules.ToList()));
            }

            agents = null;
            rules.Clear();
            inRules = false;
        }

        foreach (var rawLine in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var field = line[..separator].Trim().ToLowerInvariant();
            var value = line[(separator + 1)..].Trim();

            if (field == "user-agent")
            {
                if (inRules)
                {
                    Flush();
                }

                agents ??= [];
                agents.Add(value);
                continue;
            }

            if (field is not ("allow" or "disallow"))
            {
                continue;
            }

            inRules = true;
            if (string.IsNullOrEmpty(value))
            {
                // Пустой Disallow: — разрешено всё (ограничений нет).
                continue;
            }

            rules.Add((field == "allow", value));
        }

        Flush();

        var agentToken = userAgent.Split('/')[0].Trim();
        var specific = groups
            .Where(group => group.Agents.Any(agent =>
                !string.Equals(agent, "*", StringComparison.Ordinal)
                && (string.Equals(agent, agentToken, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(agent, userAgent, StringComparison.OrdinalIgnoreCase))))
            .SelectMany(group => group.Rules)
            .ToList();

        if (specific.Count > 0)
        {
            return new RobotsRules(specific);
        }

        var wildcard = groups
            .Where(group => group.Agents.Any(agent => agent == "*"))
            .SelectMany(group => group.Rules)
            .ToList();

        return wildcard.Count > 0 ? new RobotsRules(wildcard) : AllowAll;
    }

    /// <summary>Разрешён ли путь. При равенстве длины правил приоритет у Allow.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var path = uri.PathAndQuery;
        var bestLength = -1;
        var allowed = true;

        foreach (var (allow, pattern) in _rules)
        {
            if (!Matches(pattern, path) || pattern.Length < bestLength)
            {
                continue;
            }

            if (pattern.Length == bestLength && !allow)
            {
                continue;
            }

            bestLength = pattern.Length;
            allowed = allow;
        }

        return allowed;
    }

    private static bool Matches(string pattern, string path)
    {
        var regex = "^" + PatternToRegex(pattern);
        return Regex.IsMatch(path, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string PatternToRegex(string pattern)
    {
        var end = pattern.EndsWith('$');
        var body = end ? pattern[..^1] : pattern;
        var regex = Regex.Escape(body).Replace("\\*", ".*", StringComparison.Ordinal);
        return regex + (end ? "$" : string.Empty);
    }
}
