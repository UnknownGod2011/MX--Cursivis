namespace Cursivis.Companion.Services;

public static class GeminiApiKeyPoolValidator
{
    private const int MinimumKeyLength = 30;
    private const int MaximumKeyLength = 256;

    public static bool TryValidate(string value, out string message)
    {
        var keys = (value ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < keys.Length; index++)
        {
            if (!IsPlausibleGeminiCredential(keys[index]))
            {
                message = $"Key {index + 1} does not look like a valid Gemini API key. Check it and paste again.";
                return false;
            }
        }

        message = string.Empty;
        return keys.Length > 0;
    }

    public static bool IsPlausibleGeminiCredential(string? value)
    {
        var key = (value ?? string.Empty).Trim();
        if (key.Length is < MinimumKeyLength or > MaximumKeyLength || LooksLikePlaceholder(key))
        {
            return false;
        }

        if (!key.All(IsSafeAsciiTokenCharacter))
        {
            return false;
        }

        var alphaNumericCount = key.Count(IsAsciiLetterOrDigit);
        return alphaNumericCount >= 24 && !HasRepeatedPayload(key);
    }

    private static bool LooksLikePlaceholder(string value)
    {
        return value.Contains("PASTE_YOUR", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("DEMO_KEY", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("demo-key", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("YOUR_API_KEY", StringComparison.OrdinalIgnoreCase) ||
               value.All(character => character is 'x' or 'X' or '-' or '_' or '.');
    }

    private static bool HasRepeatedPayload(string value)
    {
        var payload = value.StartsWith("AQ.", StringComparison.Ordinal)
            ? value[3..]
            : value.StartsWith("AIza", StringComparison.Ordinal)
                ? value[4..]
                : value;

        return payload.Length > 0 && payload.All(character => character == payload[0]);
    }

    private static bool IsSafeAsciiTokenCharacter(char character)
    {
        return IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.';
    }

    private static bool IsAsciiLetterOrDigit(char character)
    {
        return character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
