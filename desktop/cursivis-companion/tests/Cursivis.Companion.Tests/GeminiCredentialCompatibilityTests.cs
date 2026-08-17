using Cursivis.Companion.Services;
using Cursivis.Companion.Models;
using System.IO;
using Xunit;

namespace Cursivis.Companion.Tests;

public sealed class GeminiCredentialCompatibilityTests
{
    private static readonly string LegacyKey = "AIza" + string.Concat(Enumerable.Repeat("Abc123_-", 5));
    private static readonly string AuthKey = "AQ." + string.Concat(Enumerable.Repeat("Zx9_.-", 8));

    [Fact]
    public void AcceptsLegacyAndAuthKeysInMixedPools()
    {
        var accepted = GeminiApiKeyPoolValidator.TryValidate(
            $"  {LegacyKey}\r\n; {AuthKey}  ",
            out var message);

        Assert.True(accepted, message);
    }

    [Theory]
    [InlineData("paste_your_api_key_here")]
    [InlineData("this is a sentence instead of a credential")]
    [InlineData("AQ.short")]
    public void RejectsObviousGarbage(string value)
    {
        Assert.False(GeminiApiKeyPoolValidator.IsPlausibleGeminiCredential(value));
    }

    [Fact]
    public void RejectsUnicodeAndDegenerateTokens()
    {
        var unicode = "AQ." + new string('a', 18) + "\u00e9" + new string('b', 18);
        var degenerate = "AQ." + new string('x', 36);

        Assert.False(GeminiApiKeyPoolValidator.IsPlausibleGeminiCredential(unicode));
        Assert.False(GeminiApiKeyPoolValidator.IsPlausibleGeminiCredential(degenerate));
    }

    [Fact]
    public void RedactsBothSupportedCredentialFormats()
    {
        var redacted = CredentialRedactor.Redact($"legacy={LegacyKey} auth={AuthKey}?key={AuthKey}");

        Assert.DoesNotContain(LegacyKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthKey, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED_API_KEY]", redacted, StringComparison.Ordinal);
        Assert.Contains("key=[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistsMixedPoolsWithDpapiProtection()
    {
        var profileDirectory = Path.Combine(Path.GetTempPath(), "CursivisCredentialTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new RuntimeLaunchProfileService(profileDirectory);
            await service.SaveAsync(new RuntimeLaunchProfile
            {
                ApiKey = LegacyKey,
                ApiKeys = $"{LegacyKey},{AuthKey}"
            });

            var persisted = await File.ReadAllTextAsync(Path.Combine(profileDirectory, "runtime-profile.json"));
            var loaded = await service.TryLoadAsync();

            Assert.DoesNotContain(LegacyKey, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthKey, persisted, StringComparison.Ordinal);
            Assert.NotNull(loaded);
            Assert.Equal(LegacyKey, loaded.ApiKey);
            Assert.Equal($"{LegacyKey},{AuthKey}", loaded.ApiKeys);
        }
        finally
        {
            if (Directory.Exists(profileDirectory))
            {
                Directory.Delete(profileDirectory, recursive: true);
            }
        }
    }
}
