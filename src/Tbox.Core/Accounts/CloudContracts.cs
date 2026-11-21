using System.Security.Cryptography;
using System.Text;
using Tbox.Core.Translation;

namespace Tbox.Core.Accounts;

public static class CloudProtocol
{
    public const int MaximumCaptureBytes = 32 * 1024 * 1024;
    public const int MaximumCaptureCount = 50;
    public const long MaximumHistoryBytes = 1024L * 1024 * 1024;
    public const int HistoryDays = 7;
    public const string Theme = "theme";
    public const string FailureNotifications = "failureNotifications";
    public const string TranslationLanguage = "translationLanguage";

    public static string RandomSecret() => Encode(RandomNumberGenerator.GetBytes(32));
    public static string Challenge(string verifier) => Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Proof(string purpose, Guid deviceId, string secret) =>
        Encoding.ASCII.GetBytes($"tbox-v1:{purpose}:{deviceId:D}:{secret}");

    public static bool IsSecret(string? value) => value is { Length: 43 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsPreference(string key, string value) => key switch
    {
        Theme => value is "system" or "light" or "dark",
        FailureNotifications => value is "true" or "false",
        TranslationLanguage => TranslationPolicy.Languages.Any(language => language.Id == value),
        _ => false
    };
}

public sealed record DeviceEnrollment(Guid DeviceId, string Name, string Platform, string PublicKey);
public sealed record ExchangeRequest(string Code, string Verifier, string Signature, DeviceEnrollment Device);
public sealed record RefreshRequest(Guid DeviceId, string RefreshToken, string Signature);
public sealed record CloudSession(string AccessToken, DateTimeOffset AccessExpires, string RefreshToken, string Login);
public sealed record CloudDevice(Guid Id, string Name, string Platform, DateTimeOffset? LastSeen);

public sealed record CloudPreference(string Key, string Value, long Revision);
public sealed record PreferenceEdit(string Key, string Value, long Revision);
public sealed record PreferenceResult(CloudPreference Preference, bool Conflict);
public sealed record CaptureListing(Guid Id, DateTimeOffset CreatedAt, int Bytes);
