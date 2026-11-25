#nullable enable

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Cursivis.Security;

internal static class LocalIpc
{
    internal const string Trigger = "trigger";
    internal const string Haptics = "haptics";
    internal const string Backend = "backend";
    internal const string BrowserAgent = "browser-agent";
    internal const string BrowserBridge = "browser-bridge";
    internal const string BrowserRelay = "browser-relay";
    internal const string Header = "X-Cursivis-IPC";
    internal const string AckHeader = "X-Cursivis-IPC-Ack";
    internal const string ServiceKeyVariable = "CURSIVIS_LOCAL_IPC_KEY";
    internal const string RelayKeyVariable = "CURSIVIS_LOCAL_IPC_RELAY_KEY";
    internal const string CredentialRegistryKey = @"Software\Cursivis\LocalIpc";
    internal const string CredentialValue = "CredentialV1";

    internal static byte[] GetServiceKey(string scope)
    {
        var secret = ReadOrCreateCredential(CredentialRegistryKey);
        try
        {
            return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes("Cursivis.LocalIpc/v1/" + scope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    internal static byte[] ReadOrCreateCredential(string registryPath)
    {
        using var readKey = Registry.CurrentUser.OpenSubKey(registryPath);
        if (TryReadCredential(readKey) is { } existing)
        {
            return existing;
        }

        // A trigger can start Companion while another native client is initializing the same key.
        using var identity = WindowsIdentity.GetCurrent();
        using var mutex = new Mutex(false, $@"Global\Cursivis.LocalIpc.v1.{identity.User?.Value}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
            {
                throw new IOException("Cursivis could not open its local connection credential. Try again.");
            }
        }
        catch (AbandonedMutexException)
        {
            // Ownership was acquired. Recheck the protected value before creating one.
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            if (TryReadCredential(key) is { } saved)
            {
                return saved;
            }

            var secret = RandomNumberGenerator.GetBytes(32);
            try
            {
                var protectedSecret = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
                key.SetValue(CredentialValue, protectedSecret, RegistryValueKind.Binary);
                return secret;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(secret);
                throw;
            }
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static byte[]? TryReadCredential(RegistryKey? key)
    {
        if (key?.GetValue(CredentialValue) is not byte[] { Length: > 0 and <= 4096 } protectedSecret)
        {
            return null;
        }

        try
        {
            var secret = ProtectedData.Unprotect(protectedSecret, null, DataProtectionScope.CurrentUser);
            if (secret.Length == 32)
            {
                return secret;
            }

            CryptographicOperations.ZeroMemory(secret);
        }
        catch (CryptographicException)
        {
            // An unusable credential can be repaired by the same Windows user.
        }

        return null;
    }

    internal static bool IsLocal(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme is "http" or "ws" &&
        uri.Host is "127.0.0.1" or "localhost" && uri.UserInfo.Length == 0;

    internal static Uri Canonicalize(Uri uri) =>
        IsLocal(uri) && uri.Host == "localhost" ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri : uri;

    internal static HttpClient CreateHttpClient(Uri endpoint, string scope, TimeSpan timeout)
    {
        endpoint = Canonicalize(endpoint);
        var handler = IsLocal(endpoint)
            ? new LocalHttpHandler(endpoint, scope)
            : (HttpMessageHandler)new HttpClientHandler();
        return new HttpClient(handler) { BaseAddress = endpoint, Timeout = timeout };
    }

    internal static async Task ConnectAsync(
        ClientWebSocket socket, Uri endpoint, string scope, CancellationToken cancellationToken)
    {
        endpoint = Canonicalize(endpoint);
        if (!IsLocal(endpoint))
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return;
        }

        var key = GetServiceKey(scope);
        try
        {
            var proof = SignRequest(key, "GET", endpoint.Authority, endpoint.PathAndQuery);
            socket.Options.Proxy = NoProxy;
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader(Header, proof);
            await socket.ConnectAsync(endpoint, cancellationToken);
            if (socket.HttpResponseHeaders is not { } headers ||
                !headers.TryGetValue(AckHeader, out var values) ||
                !MatchesAck(key, proof, values))
            {
                socket.Abort();
                throw new WebSocketException("Cursivis could not verify its local connection. Update Companion and the Logitech plugin together.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // Empty proxy with explicit bypass keeps local credentials off system proxies.
    private static readonly IWebProxy NoProxy = new WebProxy();

    internal static string SignRequest(byte[] key, string method, string authority, string target)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var signature = Mac(key, $"request\n{method}\n{authority}\n{target}\n{timestamp}\n{nonce}");
        return $"v1.{timestamp}.{nonce}.{signature}";
    }

    internal static string CreateAck(byte[] key, string proof) => Mac(key, "response\n" + proof);

    private static bool MatchesAck(byte[] key, string proof, IEnumerable<string> values) =>
        values.Take(2).ToArray() is [var ack] && FixedTimeMatch(CreateAck(key, proof), ack);

    private static string Mac(byte[] key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static bool FixedTimeMatch(string expected, string actual) =>
        IsLowerHex(actual, 64) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal sealed class RequestAuthenticator(byte[] key)
    {
        private const int ProofLifetimeSeconds = 30;
        private readonly Dictionary<string, long> _acceptedNonces = new(StringComparer.Ordinal);
        private readonly object _gate = new();
        private long _nextPrune;

        internal bool TryAccept(string? proof, string method, string authority, string target, out string ack)
        {
            ack = string.Empty;
            if (proof is null || proof.Length != 111 ||
                proof.Split('.') is not ["v1", var timestamp, var nonce, var signature] ||
                timestamp.Length != 10 ||
                !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                !IsLowerHex(nonce, 32))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (seconds < now - ProofLifetimeSeconds || seconds > now + 5 ||
                !FixedTimeMatch(Mac(key, $"request\n{method}\n{authority}\n{target}\n{timestamp}\n{nonce}"), signature))
            {
                return false;
            }

            lock (_gate)
            {
                if (now >= _nextPrune)
                {
                    foreach (var expired in _acceptedNonces.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray())
                    {
                        _acceptedNonces.Remove(expired);
                    }
                    _nextPrune = now + 5;
                }

                if (_acceptedNonces.Count >= 8192 || !_acceptedNonces.TryAdd(nonce, seconds + ProofLifetimeSeconds))
                {
                    return false;
                }
            }

            ack = CreateAck(key, proof);
            return true;
        }
    }

    private sealed class LocalHttpHandler(Uri endpoint, string scope) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PlaintextStreamFilter = (context, token) => VerifyServiceAsync(context, endpoint, scope, token)
    })
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not { } uri || !IsLocal(uri) || uri.Authority != endpoint.Authority)
            {
                throw new HttpRequestException("Cursivis rejected an unexpected local service address.");
            }

            var key = GetServiceKey(scope);
            try
            {
                var proof = SignRequest(key, request.Method.Method, uri.Authority, uri.PathAndQuery);
                request.Headers.Add(Header, proof);
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Headers.TryGetValues(AckHeader, out var values) && MatchesAck(key, proof, values))
                {
                    return response;
                }

                response.Dispose();
                throw new HttpRequestException("Cursivis could not verify the local service. Run Companion Setup to repair the connection.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static async ValueTask<Stream> VerifyServiceAsync(
        SocketsHttpPlaintextStreamFilterContext context, Uri endpoint, string scope, CancellationToken cancellationToken)
    {
        // Authenticate the peer on this TCP connection before HttpClient writes a private request.
        var target = context.InitialRequestMessage.RequestUri!.PathAndQuery;
        var key = GetServiceKey(scope);
        try
        {
            var proof = SignRequest(key, "HEAD", endpoint.Authority, target);
            var stream = context.PlaintextStream;
            var probe = Encoding.ASCII.GetBytes(
                $"HEAD {target} HTTP/1.1\r\nHost: {endpoint.Authority}\r\n{Header}: {proof}\r\n\r\n");
            await stream.WriteAsync(probe, cancellationToken).ConfigureAwait(false);

            var buffer = new byte[4096];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                length += count;
                var response = Encoding.ASCII.GetString(buffer, 0, length);
                var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    continue;
                }
                var lines = response[..end].Split("\r\n");
                var prefix = AckHeader + ":";
                if (end + 4 == response.Length && lines[0].StartsWith("HTTP/1.1 204 ", StringComparison.Ordinal) &&
                    MatchesAck(key, proof, lines.Skip(1)
                        .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(line => line[prefix.Length..].Trim())))
                {
                    return stream;
                }
                break;
            }

            throw new HttpRequestException("Cursivis could not verify the local service. Run Companion Setup to repair the connection.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
