using System.Net;
using Cursivis.Security;

namespace Cursivis.Companion.Services;

internal sealed class LocalWebSocketAccess(Uri endpoint, byte[] key)
{
    private readonly LocalIpc.RequestAuthenticator _authenticator = new(key);

    internal bool TryAuthorize(HttpListenerContext context)
    {
        var request = context.Request;
        var status = HttpStatusCode.Forbidden;
        if (IPAddress.Loopback.Equals(request.RemoteEndPoint?.Address) &&
            string.Equals(request.Headers["Host"], endpoint.Authority, StringComparison.Ordinal) &&
            request.Headers.GetValues("Origin") is null &&
            request.Headers.GetValues("Sec-Fetch-Site") is null &&
            request.Headers.GetValues("Sec-Fetch-Mode") is null)
        {
            if (request.RawUrl != endpoint.AbsolutePath)
            {
                status = HttpStatusCode.NotFound;
            }
            else if (!request.IsWebSocketRequest || request.HttpMethod != "GET")
            {
                status = HttpStatusCode.BadRequest;
            }
            else if (request.Headers.GetValues(LocalIpc.Header) is [var proof] &&
                     _authenticator.TryAccept(proof, request.HttpMethod, endpoint.Authority, request.RawUrl, out var ack))
            {
                context.Response.Headers[LocalIpc.AckHeader] = ack;
                return true;
            }
            else
            {
                status = HttpStatusCode.Unauthorized;
            }
        }

        context.Response.StatusCode = (int)status;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Close();
        return false;
    }
}
