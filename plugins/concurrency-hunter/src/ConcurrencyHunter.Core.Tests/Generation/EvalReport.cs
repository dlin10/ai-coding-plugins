using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>One unit an eval set's recorded count counts, and its verdict.</summary>
/// <param name="Id">The declaration id.</param>
/// <param name="Verdict"><see cref="EvalReport.EXACT"/>, <see cref="EvalReport.WIDER"/> or <see cref="EvalReport.UNSAFE"/>.</param>
/// <param name="Group">The reporting group of a set counted by group, else <c>null</c>.</param>
internal sealed record ReportLine(string Id, string Verdict, string? Group = null);

/// <summary>The verdict report: the one place each eval states an answer's verdict. The exact counts the snapshot records are the
/// numbers of <see cref="EXACT"/> lines, and under <c>CH_MODEL_EVALS_REPORT</c> the evals write their lines as <c>&lt;set&gt;.json</c>
/// into the folder it names, for the gate's catalog routing.</summary>
internal static class EvalReport
{
    public const string VARIABLE = "CH_MODEL_EVALS_REPORT";
    public const string EXACT = "exact";
    public const string WIDER = "wider";
    public const string UNSAFE = "unsafe";

    /// <summary>The folder <c>CH_MODEL_EVALS_REPORT</c> names, or <c>null</c> when it names none.</summary>
    public static string? Folder => Environment.GetEnvironmentVariable(VARIABLE) is { Length: > 0 } folder ? folder : null;

    /// <summary>The verdict of a unit: <see cref="UNSAFE"/> for an unsafe narrowing, <see cref="EXACT"/> for an exact answer,
    /// <see cref="WIDER"/> otherwise.</summary>
    /// <param name="isExact">Whether the answer is exact.</param>
    /// <param name="isUnsafe">Whether the answer is an unsafe narrowing.</param>
    public static string Verdict(bool isExact, bool isUnsafe) => isUnsafe ? UNSAFE : isExact ? EXACT : WIDER;

    /// <summary>The number of <see cref="EXACT"/> lines.</summary>
    /// <param name="lines">The report lines.</param>
    public static int Exact(IEnumerable<ReportLine> lines) => lines.Count(line => line.Verdict == EXACT);

    /// <summary>Writes a set's lines as <c>&lt;set&gt;.json</c> into a folder, a JSON array of <c>id</c>, <c>verdict</c> and, for a
    /// grouped set, <c>group</c>; nothing when the folder is <c>null</c>.</summary>
    /// <param name="folder">The report folder, or <c>null</c>.</param>
    /// <param name="set">The set's name: <c>members</c>, <c>effects</c>, <c>linq</c> or <c>accessors</c>.</param>
    /// <param name="lines">The set's lines.</param>
    public static void Write(string? folder, string set, IEnumerable<ReportLine> lines)
    {
        var array = new JsonArray();
        foreach (var line in lines)
        {
            var node = new JsonObject { ["id"] = line.Id, ["verdict"] = line.Verdict };
            if (line.Group is not null)
                node["group"] = line.Group;
            array.Add(node);
        }
        WriteFile(folder, $"{set}.json", array);
    }

    /// <summary>Writes a list of names as a JSON array of strings into a folder; nothing when the folder is <c>null</c>.</summary>
    /// <param name="folder">The report folder, or <c>null</c>.</param>
    /// <param name="file">The file name.</param>
    /// <param name="names">The names.</param>
    public static void WriteNames(string? folder, string file, IEnumerable<string> names) =>
        WriteFile(folder, file, new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()));

    /// <summary>The lines a written <c>&lt;set&gt;.json</c> holds.</summary>
    /// <param name="path">The file.</param>
    public static IReadOnlyList<ReportLine> Read(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsArray()
                .Select(node => new ReportLine((string)node!["id"]!, (string)node["verdict"]!, (string?)node["group"]))
                .ToArray();

    private static void WriteFile(string? folder, string file, JsonArray content)
    {
        if (folder is null)
            return;
        Directory.CreateDirectory(folder);
        var text = content.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
        File.WriteAllBytes(Path.Combine(folder, file), new UTF8Encoding(false).GetBytes(text));
    }
}
