using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Sources;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

public sealed partial class GeneratorBoundaryTests
{
    [Theory]
    [InlineData("environment", false)]
    [InlineData("default", false)]
    [InlineData("http-handler", false)]
    [InlineData("environment", true)]
    [InlineData("default", true)]
    [InlineData("http-handler", true)]
    public async Task Source_provider_uses_the_default_proxy_without_authenticating_after_407(string configuration, bool symbols)
    {
        await using var proxy = new ChallengeProxy();
        using var library = new SourceLibrary(new Uri("https://127.0.0.2:9/"), adjacent: !symbols, documentCount: 1);
        using var another = new SourceLibrary(new Uri("https://127.0.0.2:9/"), adjacent: !symbols, documentCount: 1, crlf: true);
        var probe = Path.Combine(library.Root, "ConcurrencyHunter.Cli.Tests.dll");
        var assemblies = new[] { typeof(GenerateCommand).Assembly, typeof(LibrarySources).Assembly,
                                 typeof(Analysis.AnalysisResult).Assembly, typeof(Compilation).Assembly, typeof(CSharpCompilation).Assembly };
        foreach (var assembly in assemblies)
            File.Copy(assembly.Location, Path.Combine(library.Root, Path.GetFileName(assembly.Location)));
        var compilation = CSharpCompilation.Create("ConcurrencyHunter.Cli.Tests", [CSharpSyntaxTree.ParseText(PROXY_PROBE)],
            StubAssemblies.PlatformWithout([]).Concat(assemblies.Select(assembly => MetadataReference.CreateFromFile(assembly.Location))),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var emitted = compilation.Emit(probe);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        File.WriteAllText(Path.ChangeExtension(probe, ".runtimeconfig.json"), JsonSerializer.Serialize(new
        {
            runtimeOptions = new { tfm = $"net{Environment.Version.Major}.0", framework = new { name = "Microsoft.NETCore.App", version = $"{Environment.Version.Major}.0.0" } }
        }));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { probe, configuration, library.Dll, proxy.Address.AbsoluteUri, another.Dll, symbols.ToString() })
            start.ArgumentList.Add(argument);
        // A fresh process is essential: HttpClient.DefaultProxy captures the environment on first use.
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
            start.Environment.Remove(name);
        start.Environment["HTTP_PROXY"] = new UriBuilder(proxy.Address) { UserName = "proxy-user", Password = "proxy-secret" }.Uri.AbsoluteUri;
        start.Environment["HTTPS_PROXY"] = start.Environment["HTTP_PROXY"];
        start.Environment["NO_PROXY"] = "";
        start.Environment["LOCALAPPDATA"] = Path.Combine(library.Root, "localappdata");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, $"Proxy probe exited {process.ExitCode}: {await stdout}{await stderr}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var requests = proxy.Requests.ToArray();
        // The control retries with credentials; each production run contacts each host only once, across two libraries.
        Assert.Equal(symbols ? 6 : 4, requests.Length);
        Assert.All(requests, request => Assert.StartsWith("CONNECT ", request, StringComparison.Ordinal));
        if (symbols)
        {
            Assert.Equal(2, requests.Count(request => request.StartsWith("CONNECT msdl.microsoft.com:443 ", StringComparison.Ordinal)));
            Assert.Equal(2, requests.Count(request => request.StartsWith("CONNECT symbols.nuget.org:443 ", StringComparison.Ordinal)));
        }
        else
            Assert.All(requests, request => Assert.StartsWith("CONNECT 127.0.0.2:9 ", request, StringComparison.Ordinal));
        Assert.DoesNotContain("Proxy-Authorization:", requests[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Proxy-Authorization: Basic ", requests[1], StringComparison.OrdinalIgnoreCase);
        Assert.All(requests.Skip(2), request =>
        {
            Assert.DoesNotContain("Proxy-Authorization:", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\r\nAuthorization:", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\r\nCookie:", request, StringComparison.OrdinalIgnoreCase);
        });
        var cache = start.Environment["LOCALAPPDATA"]!;
        var files = Directory.Exists(cache) ? Directory.GetFiles(cache, "*", SearchOption.AllDirectories) : [];
        Assert.Equal(symbols ? 0 : 2, files.Length);
        Assert.All(files, file => Assert.Equal("symbols.pdb", Path.GetFileName(file)));
    }

    // This child uses the actual CLI factory and default-proxy discovery, with only loopback destinations and a temporary cache.
    private const string PROXY_PROBE = """
        using System;
        using System.IO;
        using System.Net;
        using System.Net.Http;
        using System.Threading.Tasks;
        using ConcurrencyHunter;
        using ConcurrencyHunter.Providers.LibraryModels.Sources;

        public static class ProxyProbe
        {
            public static async Task Main(string[] args)
            {
                var address = new Uri(args[2]);
                if (args[0] != "environment")
                    HttpClient.DefaultProxy = new WebProxy(new UriBuilder(address) { UserName = "uri-user", Password = "uri-secret" }.Uri)
                    {
                        Credentials = new NetworkCredential("proxy-user", "proxy-secret")
                    };
                foreach (var scheme in new[] { "http", "https" })
                {
                    var destination = new Uri(scheme + "://127.0.0.2:9/");
                    var selected = HttpClient.DefaultProxy.GetProxy(destination);
                    if (selected == null || selected.Port != address.Port || HttpClient.DefaultProxy.IsBypassed(destination) ||
                        HttpClient.DefaultProxy.Credentials?.GetCredential(selected, "Basic")?.UserName != "proxy-user")
                        throw new Exception("The configured proxy and credentials were not discovered for " + scheme);
                }
                using (var control = new HttpClient(new SocketsHttpHandler { UseProxy = true }))
                {
                    try { await control.GetAsync("https://127.0.0.2:9/"); }
                    catch (HttpRequestException) { }
                }
                using (var offline = SourceProvider.Create(true))
                    await offline.GetAsync(args[1]);
                // Retrying with another run must not acquire credentials after the proxy's challenge, either.
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var sources = args[0] == "http-handler"
                        ? new LibrarySources(false, Path.Combine(SourceProvider.ApplicationData, "sources"), handler: new HttpClientHandler
                        {
                            Credentials = CredentialCache.DefaultCredentials, UseDefaultCredentials = true, PreAuthenticate = true,
                            DefaultProxyCredentials = new NetworkCredential("default-user", "default-secret")
                        })
                        : SourceProvider.Create(false);
                    var result = await sources.GetAsync(args[1]);
                    var reason = bool.Parse(args[4]) ? LibraryCodeReason.NoPdb : LibraryCodeReason.PersistentFailure;
                    if (result.Kind != LibraryCodeKind.DecompiledBest || result.Reason != reason)
                        throw new Exception("CONNECT 407 was not a stable refusal: " + result.Kind + "/" + result.Reason);
                    var host = bool.Parse(args[4]) ? "msdl.microsoft.com" : "127.0.0.2";
                    if (result.Detail == null || !result.Detail.Contains(host) || !result.Detail.Contains("HTTP 407"))
                        throw new Exception("CONNECT diagnostic was lost: " + result.Detail);
                    var requests = sources.Statistics.Downloads.Requests;
                    var next = await sources.GetAsync(args[3]);
                    if (requests == 0 || sources.Statistics.Downloads.Requests != requests || next.Kind != result.Kind || next.Reason != reason)
                        throw new Exception("A denied host was requested again in the same run");
                    if (next.Detail != result.Detail)
                        throw new Exception("The skipped host lost its original request diagnostic");
                }
            }
        }
        """;

    private sealed class ChallengeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _serving;
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal Uri Address { get; }

        internal ChallengeProxy()
        {
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _serving = ServeAsync();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var headers = new StringBuilder();
                    while (await reader.ReadLineAsync(_stopping.Token) is { Length: > 0 } line)
                        headers.Append(line).Append("\r\n");
                    Requests.Enqueue(headers.ToString());
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"loopback\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _stopping.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _serving;
            _stopping.Dispose();
        }
    }
}
