#nullable disable

namespace Cursivis.Companion.LiveMode;

using System.Diagnostics;
using System.IO;

public sealed class BrowserLauncherService
{
    private static readonly Dictionary<string, string[]> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = ["Google Chrome", "Chrome"],
        ["edge"] = ["Microsoft Edge", "Edge"],
        ["brave"] = ["Brave"],
        ["firefox"] = ["Mozilla Firefox", "Firefox"],
        ["opera"] = ["Opera"],
        ["vivaldi"] = ["Vivaldi"],
        ["arc"] = ["Arc"],
    };

    private readonly AppResolverService _apps;
    private readonly LiveModeSettingsService _settings;

    public BrowserLauncherService(AppResolverService apps, LiveModeSettingsService settings)
    {
        _apps = apps;
        _settings = settings;
    }

    public bool TryOpen(string url, string requestedBrowser, out string message, out string browserUsed)
    {
        message = "";
        browserUsed = "";
        if (!TryNormalizeUrl(url, out var uri))
        {
            message = "Only absolute http and https links can be opened.";
            return false;
        }

        var browser = NormalizeBrowser(requestedBrowser);
        if (string.IsNullOrWhiteSpace(browser))
            browser = NormalizeBrowser(_settings.Current.PreferredBrowser);
        if (string.IsNullOrWhiteSpace(browser))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
                browserUsed = "default browser";
                message = $"Opened {uri.Host} in the default browser.";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        var candidate = FindBrowserExecutable(browser);
        if (candidate == null)
        {
            message = $"{DisplayBrowser(browser)} is not installed or does not expose a safe executable. Ask whether to use the default browser.";
            return false;
        }

        try
        {
            var path = candidate.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase)
                ? candidate.LaunchTarget[5..]
                : candidate.LaunchTarget;
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(uri.ToString());
            Process.Start(info);
            browserUsed = DisplayBrowser(browser);
            message = $"Opened {uri.Host} in {browserUsed}.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"{DisplayBrowser(browser)} could not open the link: {ex.Message}";
            return false;
        }
    }

    public AppCandidate FindBrowserExecutable(string browser)
    {
        var normalized = NormalizeBrowser(browser);
        if (string.IsNullOrWhiteSpace(normalized) || !BrowserNames.TryGetValue(normalized, out var names))
            return null;

        var candidates = _apps.Discover();
        return candidates
            .Where(candidate => candidate.LaunchTarget.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => Path.GetExtension(candidate.LaunchTarget[5..])
                .Equals(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(candidate => AppResolverService.IsTrustedLaunchPath(candidate.LaunchTarget[5..]))
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = names.Max(name => FuzzyMatcher.Score(name, candidate.Name)),
            })
            .Where(item => item.Score >= 0.78)
            .OrderByDescending(item => item.Score)
            .Select(item => item.Candidate)
            .FirstOrDefault();
    }

    public static string BuildSearchUrl(string query, string service)
    {
        var encoded = Uri.EscapeDataString((query ?? "").Trim());
        return FuzzyMatcher.Normalize(service) switch
        {
            "youtube" or "youtube music" => $"https://www.youtube.com/results?search_query={encoded}",
            "github" => $"https://github.com/search?q={encoded}",
            "bing" => $"https://www.bing.com/search?q={encoded}",
            _ => $"https://www.google.com/search?q={encoded}",
        };
    }

    public static string NormalizeBrowser(string browser)
    {
        var normalized = FuzzyMatcher.Normalize(browser);
        return normalized switch
        {
            "" or "default" or "default browser" => "",
            "google chrome" or "chrome" => "chrome",
            "microsoft edge" or "edge" => "edge",
            "brave browser" or "brave" => "brave",
            "mozilla firefox" or "firefox" => "firefox",
            "opera browser" or "opera" => "opera",
            "vivaldi browser" or "vivaldi" => "vivaldi",
            "arc browser" or "arc" => "arc",
            _ => normalized,
        };
    }

    public static bool TryNormalizeUrl(string url, out Uri uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string DisplayBrowser(string browser) => NormalizeBrowser(browser) switch
    {
        "chrome" => "Chrome",
        "edge" => "Edge",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "arc" => "Arc",
        _ => browser,
    };
}

