using System.Security.Cryptography;
using System.Text;
using PlanForgeFlow.Cli;

namespace PlanForgeFlow.Infrastructure.Process;

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

}
