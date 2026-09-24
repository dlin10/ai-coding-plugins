using static ConcurrencyHunter.Providers.LibrarySemantics.LibraryEffect;

namespace ConcurrencyHunter.Providers.LibrarySemantics;

/// <summary><c>Newtonsoft.Json</c> 13 (R2), the mirror of <c>System.Text.Json</c>: every <c>SerializeObject</c> reads its value
/// deep, every <c>DeserializeObject</c> touches nothing. Converters and settings run no user code in the effect.</summary>
internal static class NewtonsoftJsonFamily
{
    private static readonly SupportedAssemblyVersion[] Assemblies =
        [new("Newtonsoft.Json", new Version(13, 0, 0, 0), new Version(14, 0, 0, 0))];

    internal static IEnumerable<LibraryMember> Members =>
    [
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object)~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting)~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonConverter[])~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonConverter[])~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.JsonSerializerSettings)~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.JsonSerializerSettings)~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)~System.String"),
        Serialize("M:Newtonsoft.Json.JsonConvert.SerializeObject(System.Object,System.Type,Newtonsoft.Json.Formatting,Newtonsoft.Json.JsonSerializerSettings)~System.String"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String)~System.Object"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,Newtonsoft.Json.JsonSerializerSettings)~System.Object"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type)~System.Object"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String)~``0"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String,Newtonsoft.Json.JsonConverter[])~``0"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject``1(System.String,Newtonsoft.Json.JsonSerializerSettings)~``0"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type,Newtonsoft.Json.JsonConverter[])~System.Object"),
        Known("M:Newtonsoft.Json.JsonConvert.DeserializeObject(System.String,System.Type,Newtonsoft.Json.JsonSerializerSettings)~System.Object")
    ];

    private static LibraryMember Serialize(string id) => new(id, Assemblies, [DeepReadOf("value")]);

    private static LibraryMember Known(string id) => new(id, Assemblies, []);
}
