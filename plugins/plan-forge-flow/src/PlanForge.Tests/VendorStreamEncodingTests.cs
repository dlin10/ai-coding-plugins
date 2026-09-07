using System.Text;
using PlanForge.Infrastructure;
using Xunit;

namespace PlanForge.Tests;

/// <summary>
/// What a vendor writes is what the run has to read back. Run 20260902-224201-7bf03b read every
/// vendor's stdout through the console code page: an MCP host starts this server without a
/// console, the fallback was CP437, and the UTF-8 bytes of an em dash came back as three
/// characters of mojibake — in the run log, in the critic's findings and in the builder's
/// verification evidence alike.
/// </summary>
public sealed class VendorStreamEncodingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "planforge-tests", Guid.NewGuid().ToString("n"));

    public VendorStreamEncodingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The dashes are the two this run actually lost; the Cyrillic stands for every plan and
    /// finding that is not written in English, which the same fault would have destroyed whole.
    /// </summary>
    [Theory]
    [InlineData("an em dash — mid sentence")]
    [InlineData("an en dash – mid sentence")]
    [InlineData("Проверка не выполнена")]
    [InlineData("curly “quotes” and an ellipsis…")]
    public async Task Vendor_output_survives_the_pipe_unchanged(string written)
    {
        var path = Path.Combine(_directory, "vendor.txt");
        await File.WriteAllTextAsync(path, written + "\n", new UTF8Encoding(false));

        var lines = await StreamingProcess.CollectAsync(Emitting(path), TimeSpan.FromMinutes(1),
                                                        CancellationToken.None);

        Assert.Equal(written, Assert.Single(lines));
    }

    /// <summary>A process that copies a file's bytes to stdout, standing in for a vendor CLI.</summary>
    private ProcessSpec Emitting(string path) =>
        OperatingSystem.IsWindows()
            ? new ProcessSpec("cmd.exe", ["/c", "type", path], _directory, string.Empty)
            : new ProcessSpec("/bin/sh", ["-c", $"cat '{path}'"], _directory, string.Empty);
}
