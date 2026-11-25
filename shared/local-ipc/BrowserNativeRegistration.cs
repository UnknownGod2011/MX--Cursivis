#nullable enable

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Cursivis.Security;

internal static class BrowserNativeRegistration
{
    internal const string HostName = "com.cursivis.browser_bridge";
    private const string ExtensionScheme = "chrome-extension://";
    private static readonly string[] BrowserKeys =
    [
        @"Software\Google\Chrome", @"Software\Google\ChromeForTesting", @"Software\Microsoft\Edge",
        @"Software\BraveSoftware\Brave-Browser", @"Software\Vivaldi", @"Software\Opera Software\Opera",
        @"Software\Chromium"
    ];

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cursivis", "browser-native-host", HostName + ".json");

    internal static string? ParseCallerId(string? origin)
    {
        if (origin is null || !origin.StartsWith(ExtensionScheme, StringComparison.Ordinal))
        {
            return null;
        }
        var id = origin[ExtensionScheme.Length..];
        if (id.EndsWith('/'))
        {
            id = id[..^1];
        }
        return IsValidId(id) ? id : null;
    }

    internal static bool IsAllowedCaller(string id) => ReadApprovedIds(ManifestPath).Contains(id);

    internal static string GetUnpackedExtensionId(string directory)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (path.Length >= 2 && path[1] == ':')
        {
            path = char.ToUpperInvariant(path[0]) + path[1..];
        }
        // Chromium hashes the native UTF-16 path on Windows for unpacked extensions.
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(path));
        return string.Create(32, hash, (result, bytes) =>
        {
            for (var i = 0; i < 16; i++)
            {
                result[i * 2] = (char)('a' + (bytes[i] >> 4));
                result[i * 2 + 1] = (char)('a' + (bytes[i] & 15));
            }
        });
    }

    internal static void Register(string hostExecutable, string extensionId)
    {
        if (!IsValidId(extensionId) || !File.Exists(hostExecutable))
        {
            throw new InvalidOperationException("The Cursivis browser host or extension ID is invalid.");
        }

        var manifestPath = ManifestPath;
        var ids = ReadApprovedIds(manifestPath);
        ids.Add(extensionId);
        var manifest = JsonSerializer.Serialize(new
        {
            name = HostName,
            description = "Cursivis current-tab browser bridge",
            path = Path.GetFullPath(hostExecutable),
            type = "stdio",
            allowed_origins = ids.OrderBy(id => id, StringComparer.Ordinal).Select(id => ExtensionScheme + id + "/")
        });
        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var temporary = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, manifest);
                File.Move(temporary, manifestPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            foreach (var browser in BrowserKeys)
            {
                var keyPath = $@"{browser}\NativeMessagingHosts\{HostName}";
                using var key = currentUser.CreateSubKey(keyPath);
                if (!string.Equals(key.GetValue("") as string, manifestPath, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("", manifestPath, RegistryValueKind.String);
                }
            }
        }
    }

    private static HashSet<string> ReadApprovedIds(string manifestPath)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (new FileInfo(manifestPath) is not { Exists: true, Length: <= 65536 })
            {
                return ids;
            }
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            if (root.GetProperty("name").GetString() != HostName || root.GetProperty("type").GetString() != "stdio")
            {
                return ids;
            }
            foreach (var origin in root.GetProperty("allowed_origins").EnumerateArray())
            {
                if (origin.ValueKind == JsonValueKind.String && ParseCallerId(origin.GetString()) is { } id)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // Repair can replace an unreadable or malformed registration without granting an origin.
        }
        return ids;
    }

    private static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');
}
