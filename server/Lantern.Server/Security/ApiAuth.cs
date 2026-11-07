using System.Net;
using Lantern.Core.Api;
using Lantern.Database.Repositories;
using Lantern.Server.Security;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Security;

/// <summary>Item key the resolved <see cref="AuthContext"/> is stored under.</summary>
public static class AuthKeys
{
    public const string Context = "lantern.auth";
}

/// <summary>
/// Turns a request into an <see cref="AuthContext"/>: a paired device token, a loopback
/// caller (the desktop UI) or an anonymous LAN client.
/// </summary>
public sealed class ApiAuth
{
    private readonly DeviceRepository _devices;
    private readonly PairingService _pairing;
    private readonly ILogger<ApiAuth> _logger;

    public ApiAuth(DeviceRepository devices, PairingService pairing, ILogger<ApiAuth> logger)
    {
        _devices = devices;
        _pairing = pairing;
        _logger = logger;
    }

    public async Task<AuthContext> ResolveAsync(HttpContext http, CancellationToken ct = default)
    {
        var address = http.Connection.RemoteIpAddress;
        var ip = address?.ToString();
        var isLocal = address is not null && IPAddress.IsLoopback(address) && IsLocalRequest(http.Request);

        var token = http.Request.Headers[ApiRoutes.TokenHeader].ToString().Trim();
        if (TokenService.LooksLikeToken(token))
        {
            var hash = TokenService.Hash(token);

            var device = await _devices.GetByTokenHashAsync(hash, ct).ConfigureAwait(false);
            if (device is { Revoked: false })
            {
                return new AuthContext
                {
                    DeviceId = device.DeviceId,
                    DeviceName = device.Name,
                    IsTrusted = true,
                    IsLocalAdmin = isLocal,
                    IpAddress = ip,
                };
            }

            // A session approved with "do not remember" only lives in memory.
            var ephemeral = _pairing.ResolveEphemeralToken(hash);
            if (ephemeral is not null)
            {
                return new AuthContext
                {
                    DeviceId = ephemeral.DeviceId,
                    DeviceName = ephemeral.Name,
                    IsTrusted = true,
                    IsLocalAdmin = isLocal,
                    IpAddress = ip,
                };
            }
        }

        if (isLocal)
        {
            return new AuthContext
            {
                DeviceId = "pc-local",
                DeviceName = "This PC",
                IsLocalAdmin = true,
                IpAddress = ip,
            };
        }

        var claimedId = http.Request.Headers[ApiRoutes.DeviceIdHeader].ToString().Trim();
        if (string.IsNullOrWhiteSpace(claimedId))
        {
            return AuthContext.Anonymous(ip);
        }

        _logger.LogDebug("Unpaired request from {Device} ({Ip})", claimedId, ip);
        return new AuthContext
        {
            DeviceId = claimedId,
            DeviceName = "Unpaired device",
            IpAddress = ip,
        };
    }
    private static bool IsLocalRequest(HttpRequest request)
    {
        // A remote site must not gain PC privileges through a form submission or DNS rebinding.
        var host = request.Host.Host.Trim('[', ']');
        if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
            return false;

        if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            return false;

        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 ||
            origin.Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }
}

public static class HttpContextAuthExtensions
{
    /// <summary>The context resolved by <see cref="AuthEndpointFilter"/> (anonymous when absent).</summary>
    public static AuthContext Auth(this HttpContext http) =>
        http.Items.TryGetValue(AuthKeys.Context, out var value) && value is AuthContext context
            ? context
            : AuthContext.Anonymous(http.Connection.RemoteIpAddress?.ToString());
}
