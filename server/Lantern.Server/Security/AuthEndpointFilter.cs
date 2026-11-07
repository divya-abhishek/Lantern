using Lantern.Server.Services;

namespace Lantern.Server.Security;

/// <summary>
/// Resolves the caller on every request and rejects unpaired devices when the server is
/// configured to require pairing. Applied per endpoint group so the anonymous surface
/// (<c>/server/info</c>, <c>/auth/check</c>, the pairing handshake) stays explicit.
/// </summary>
public sealed class AuthEndpointFilter : IEndpointFilter
{
    private readonly bool _allowAnonymous;

    public AuthEndpointFilter(bool allowAnonymous) => _allowAnonymous = allowAnonymous;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var auth = await http.RequestServices
            .GetRequiredService<ApiAuth>()
            .ResolveAsync(http, http.RequestAborted)
            .ConfigureAwait(false);

        http.Items[AuthKeys.Context] = auth;

        if (_allowAnonymous) return await next(context).ConfigureAwait(false);

        var settings = http.RequestServices.GetRequiredService<SettingsStore>().Current;
        if (auth.IsAdmin || auth.IsTrusted || !settings.RequirePairing) return await next(context).ConfigureAwait(false);

        return ApiResults.Unauthorized(
            $"{settings.ServerName} requires pairing. Approve \"{auth.DeviceName}\" on the PC, then retry.");
    }
}

public static class AuthEndpointExtensions
{
    public static RouteHandlerBuilder RequireLanternAdmin(this RouteHandlerBuilder builder) =>
        builder.RequireLanternAuth().AddEndpointFilter(async (context, next) =>
            context.HttpContext.Auth().IsLocalAdmin ? await next(context) : ApiResults.Forbidden("Manage Lantern from this PC."));

    /// <summary>Endpoint reachable without a token (the handler still sees who is calling).</summary>
    public static RouteHandlerBuilder AllowLanternAnonymous(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(new AuthEndpointFilter(allowAnonymous: true));

    /// <summary>Endpoint that requires a paired device or a loopback caller.</summary>
    public static RouteHandlerBuilder RequireLanternAuth(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(new AuthEndpointFilter(allowAnonymous: false));
}
