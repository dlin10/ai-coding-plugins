using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary><c>HttpClient</c> (R2): a request reads the content or the request message it sends deep; reading a response, a
/// request's own properties or the headers touches nothing. A member that changes a client's, a request's or a header's state is
/// not known (the state of a library object is no resource), nor is <c>DelegatingHandler.SendAsync</c>, which calls an inner
/// handler that may be the user's. The client factory is declared in <c>Microsoft.Extensions.Http</c>.</summary>
internal static class HttpClientFamily
{
    private const string CLIENT = "System.Net.Http.HttpClient";
    private const string RESPONSE_TASK = "~System.Threading.Tasks.Task{System.Net.Http.HttpResponseMessage}";

    private static readonly SupportedAssemblyVersion[] Assemblies = [SupportedAssemblyVersion.Framework("System.Net.Http")];
    private static readonly SupportedAssemblyVersion[] FactoryAssemblies = [SupportedAssemblyVersion.Framework("Microsoft.Extensions.Http")];

    internal static IEnumerable<LibraryMember> Members =>
    [
        .. new[] { "System.String", "System.Uri" }.SelectMany(uri => new[]
        {
            Known($"M:{CLIENT}.GetAsync({uri}){RESPONSE_TASK}"),
            Known($"M:{CLIENT}.GetAsync({uri},System.Net.Http.HttpCompletionOption){RESPONSE_TASK}"),
            Known($"M:{CLIENT}.GetAsync({uri},System.Threading.CancellationToken){RESPONSE_TASK}"),
            Known($"M:{CLIENT}.GetAsync({uri},System.Net.Http.HttpCompletionOption,System.Threading.CancellationToken){RESPONSE_TASK}"),
            Known($"M:{CLIENT}.GetStringAsync({uri})~System.Threading.Tasks.Task{{System.String}}"),
            Known($"M:{CLIENT}.GetStringAsync({uri},System.Threading.CancellationToken)~System.Threading.Tasks.Task{{System.String}}"),
            Known($"M:{CLIENT}.PostAsync({uri},System.Net.Http.HttpContent){RESPONSE_TASK}", DeepReadOf("content")),
            Known($"M:{CLIENT}.PostAsync({uri},System.Net.Http.HttpContent,System.Threading.CancellationToken){RESPONSE_TASK}", DeepReadOf("content")),
            Known($"M:{CLIENT}.PutAsync({uri},System.Net.Http.HttpContent){RESPONSE_TASK}", DeepReadOf("content")),
            Known($"M:{CLIENT}.PutAsync({uri},System.Net.Http.HttpContent,System.Threading.CancellationToken){RESPONSE_TASK}", DeepReadOf("content"))
        }),
        Known($"M:{CLIENT}.SendAsync(System.Net.Http.HttpRequestMessage){RESPONSE_TASK}", DeepReadOf("request")),
        Known($"M:{CLIENT}.SendAsync(System.Net.Http.HttpRequestMessage,System.Threading.CancellationToken){RESPONSE_TASK}", DeepReadOf("request")),
        Known($"M:{CLIENT}.SendAsync(System.Net.Http.HttpRequestMessage,System.Net.Http.HttpCompletionOption){RESPONSE_TASK}", DeepReadOf("request")),
        Known($"M:{CLIENT}.SendAsync(System.Net.Http.HttpRequestMessage,System.Net.Http.HttpCompletionOption,System.Threading.CancellationToken){RESPONSE_TASK}",
              DeepReadOf("request")),
        Known($"M:{CLIENT}.get_DefaultRequestHeaders~System.Net.Http.Headers.HttpRequestHeaders"),
        Known("M:System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode~System.Net.Http.HttpResponseMessage"),
        Known("M:System.Net.Http.HttpResponseMessage.get_Content~System.Net.Http.HttpContent"),
        Known("M:System.Net.Http.HttpResponseMessage.get_StatusCode~System.Net.HttpStatusCode"),
        Known("M:System.Net.Http.HttpResponseMessage.get_IsSuccessStatusCode~System.Boolean"),
        Known("M:System.Net.Http.HttpResponseMessage.get_ReasonPhrase~System.String"),
        Known("M:System.Net.Http.HttpResponseMessage.get_RequestMessage~System.Net.Http.HttpRequestMessage"),
        Known("M:System.Net.Http.HttpResponseMessage.get_Headers~System.Net.Http.Headers.HttpResponseHeaders"),
        Known("M:System.Net.Http.HttpContent.ReadAsStringAsync~System.Threading.Tasks.Task{System.String}"),
        Known("M:System.Net.Http.HttpContent.ReadAsStringAsync(System.Threading.CancellationToken)~System.Threading.Tasks.Task{System.String}"),
        Known("M:System.Net.Http.HttpRequestMessage.get_Headers~System.Net.Http.Headers.HttpRequestHeaders"),
        Known("M:System.Net.Http.HttpRequestMessage.get_Method~System.Net.Http.HttpMethod"),
        Known("M:System.Net.Http.HttpRequestMessage.get_RequestUri~System.Uri"),
        Known("M:System.Net.Http.HttpRequestMessage.#ctor"),
        Known("M:System.Net.Http.HttpRequestMessage.#ctor(System.Net.Http.HttpMethod,System.Uri)"),
        Known("M:System.Net.Http.HttpRequestMessage.#ctor(System.Net.Http.HttpMethod,System.String)"),
        .. new[] { "Connect", "Delete", "Get", "Head", "Options", "Patch", "Post", "Put", "Query", "Trace" }
            .Select(method => Known($"M:System.Net.Http.HttpMethod.get_{method}~System.Net.Http.HttpMethod")),
        Known("M:System.Net.Http.Headers.HttpHeaders.Contains(System.String)~System.Boolean"),
        Known("M:System.Net.Http.Headers.HttpHeaders.TryGetValues(System.String,System.Collections.Generic.IEnumerable{System.String}@)~System.Boolean"),
        Known("M:System.Net.Http.StringContent.#ctor(System.String)"),
        Known("M:System.Net.Http.StringContent.#ctor(System.String,System.Net.Http.Headers.MediaTypeHeaderValue)"),
        Known("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding)"),
        Known("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding,System.String)"),
        Known("M:System.Net.Http.StringContent.#ctor(System.String,System.Text.Encoding,System.Net.Http.Headers.MediaTypeHeaderValue)"),
        Known("M:System.Net.Http.Headers.AuthenticationHeaderValue.#ctor(System.String)"),
        Known("M:System.Net.Http.Headers.AuthenticationHeaderValue.#ctor(System.String,System.String)"),
        Known("M:System.Net.Http.DelegatingHandler.#ctor"),
        Known("M:System.Net.Http.DelegatingHandler.#ctor(System.Net.Http.HttpMessageHandler)"),
        new LibraryMember("M:System.Net.Http.IHttpClientFactory.CreateClient(System.String)~System.Net.Http.HttpClient", FactoryAssemblies, []),
        new LibraryMember("M:System.Net.Http.HttpClientFactoryExtensions.CreateClient(System.Net.Http.IHttpClientFactory)~System.Net.Http.HttpClient",
            FactoryAssemblies, [])
    ];

    private static LibraryMember Known(string id, params LibraryEffect[] effects) => new(id, Assemblies, effects);
}
