namespace Tbox.Cloud;

internal sealed record CloudOptions(
    Uri Origin, long OwnerId, string ClientId, string ClientSecret, string Database, byte[] CaptureKey)
{
    internal static CloudOptions Load(IConfiguration config)
    {
        if (!Uri.TryCreate(Required(config, "Origin"), UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/" ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new InvalidOperationException("Cloud:Origin must be an HTTPS origin.");
        if (!long.TryParse(Required(config, "OwnerId"), out var owner) || owner <= 0)
            throw new InvalidOperationException("Cloud:OwnerId must be the positive GitHub numeric user ID.");
        var key = Convert.FromBase64String(Required(config, "CaptureKey"));
        if (key.Length != 32) throw new InvalidOperationException("Cloud:CaptureKey must contain 32 random bytes encoded as base64.");
        return new CloudOptions(origin, owner, Required(config, "ClientId"), Required(config, "ClientSecret"),
            Required(config, "Database"), key);
    }

    private static string Required(IConfiguration config, string key) =>
        config["Cloud:" + key] is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing Cloud:" + key);
}
