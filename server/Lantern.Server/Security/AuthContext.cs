using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Lantern.Core.Dtos;
using Lantern.Database;
using Microsoft.Extensions.Logging;

namespace Lantern.Server.Security;

/// <summary>Creates and hashes the bearer tokens handed out during pairing.</summary>
public static class TokenService
{
    /// <summary>High entropy, URL safe token (43 chars, 256 bits).</summary>
    public static string CreateToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Only the hash of a token is ever stored; the plain value lives on the phone.</summary>
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>Cheap shape check so an empty header never hits the database.</summary>
    public static bool LooksLikeToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) && token.Length is >= 20 and <= 256;

    /// <summary>Short human readable code the user compares with the phone, e.g. <c>7K4M-19</c>.</summary>
    public static string CreateShortCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> buffer = stackalloc char[7];
        for (var i = 0; i < 4; i++) buffer[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        buffer[4] = '-';
        for (var i = 5; i < 7; i++) buffer[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(buffer);
    }

    /// <summary>
    /// The pairing PIN: four random digits with a non-zero first digit, so it can be read off the
    /// projector and typed on the PC with a remote/keyboard. Never derived from the device id.
    /// </summary>
    public static string CreatePin()
    {
        // 1000-9999 keeps the PIN four digits and avoids a leading zero.
        var value = RandomNumberGenerator.GetInt32(1000, 10000);
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Who is calling, and how much are they allowed to do.</summary>
public sealed class AuthContext
{
    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>True when a valid bearer token belonging to a non revoked device was presented.</summary>
    public bool IsTrusted { get; init; }

    /// <summary>True for requests from this machine (loopback): the desktop UI / local tooling.</summary>
    public bool IsLocalAdmin { get; init; }

    public string? IpAddress { get; init; }

    public bool IsAdmin => IsLocalAdmin;

    public static AuthContext Anonymous(string? ipAddress) => new()
    {
        DeviceId = "anonymous",
        DeviceName = "Unknown device",
        IpAddress = ipAddress,
    };
}
