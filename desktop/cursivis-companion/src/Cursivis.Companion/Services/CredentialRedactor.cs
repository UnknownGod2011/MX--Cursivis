using System.Text.RegularExpressions;

namespace Cursivis.Companion.Services;

public static partial class CredentialRedactor
{
    public static string Redact(string? value)
    {
        var text = value ?? string.Empty;
        text = KnownGeminiKeyPattern().Replace(text, "[REDACTED_API_KEY]");
        text = BearerTokenPattern().Replace(text, "Bearer [REDACTED]");
        return QueryCredentialPattern().Replace(text, "$1[REDACTED]");
    }

    [GeneratedRegex(@"(?:AIza[A-Za-z0-9_-]{20,}|AQ\.[A-Za-z0-9._-]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex KnownGeminiKeyPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{10,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"([?&](?:key|api[_-]?key)=)[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryCredentialPattern();
}
