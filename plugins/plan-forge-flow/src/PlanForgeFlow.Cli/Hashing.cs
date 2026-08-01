using System.Security.Cryptography;
using System.Text;

namespace PlanForgeFlow;

internal static class Hashing
{
    public static string Sha256Hex(string value) => Convert.ToHexString(
                                                                        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> value) => Convert.ToHexString(
                                                                                    SHA256.HashData(value)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new CliFailure("environment", $"file exceeds the baseline size bound: {path}");
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Nonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                                           .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Base64UrlEncode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                                                                 .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }
}