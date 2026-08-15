// Table of Contents
// - Gemini API key pool validation
//   - Pool validation
//   - Single-key shape checks
//   - Placeholder rejection

namespace Cursivis.Companion.Services;

public static class GeminiApiKeyPoolValidator
{
    private const int MinimumKeyLength = 30;
    private const int MaximumKeyLength = 80;

    public static bool TryValidate(string? value, out string message)
    {
        var keys = (value ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var index = 0; index < keys.Length; index++)
        {
            if (LooksLikeGeminiApiKey(keys[index]))
            {
                continue;
            }

            message = $"Key {index + 1} does not look like a valid Gemini API key. Check it and paste again.";
            return false;
        }

        message = string.Empty;
        return keys.Length > 0;
    }

    public static bool LooksLikeGeminiApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (LooksLikePlaceholder(key) || !HasOnlySupportedCharacters(key))
        {
            return false;
        }

        return HasRecognizedGeminiApiKeyPrefix(key) &&
               key.Length is >= MinimumKeyLength and <= MaximumKeyLength;
    }

    private static bool HasRecognizedGeminiApiKeyPrefix(string key)
    {
        return key.StartsWith("AIza", StringComparison.Ordinal) ||
               key.StartsWith("AQ.", StringComparison.Ordinal);
    }

    private static bool HasOnlySupportedCharacters(string key)
    {
        return key.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    private static bool LooksLikePlaceholder(string key)
    {
        return key.Contains("PASTE_YOUR", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("DEMO_KEY", StringComparison.OrdinalIgnoreCase) ||
               key.Contains("demo-key", StringComparison.OrdinalIgnoreCase) ||
               key.All(character => character is 'x' or 'X' or '-' or '_');
    }
}
