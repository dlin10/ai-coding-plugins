using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace PlanForge.Mcp;

/// <summary>
/// The MCP Apps UI resource that <c>forge.plan.show</c> renders into. One static HTML document:
/// the host loads it once and pushes each tool result into it over the postMessage bridge, so the
/// plan itself never travels in this file.
/// </summary>
/// <remarks>
/// The document is self-contained on purpose. The host frames it in a sandbox whose CSP has no
/// allowance for an origin we did not declare, and declaring one would buy a markdown library at
/// the cost of a network dependency in the middle of the approval step. The renderer inside
/// <c>PlanCanvas.html</c> is smaller than that allowance would be worth.
/// </remarks>
[McpServerResourceType]
internal sealed class PlanCanvas
{
    internal const string ResourceUri = "ui://planforge/plan.html";

    /// <summary>
    /// Fixed by <c>LogicalName</c> in the csproj rather than derived from the folder, so moving the
    /// file cannot silently break the lookup at startup.
    /// </summary>
    private const string DocumentName = "PlanForge.Mcp.PlanCanvas.html";

    private static readonly string Document = ReadDocument();

    [McpServerResource(UriTemplate = ResourceUri,
                       Name = "plan-canvas",
                       Title = "Plan Forge plan",
                       MimeType = McpApps.HtmlMimeType)]
    [Description("The document view of a plan awaiting approval, with the working-tree drift beside it.")]
    public static string Plan() => Document;

    private static string ReadDocument()
    {
        using var stream = typeof(PlanCanvas).Assembly.GetManifestResourceStream(DocumentName)
            ?? throw new InvalidOperationException($"the canvas document '{DocumentName}' is not embedded in this build");

        // The file is UTF-8 without a BOM, and one middle dot in the header makes that load-bearing.
        using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return reader.ReadToEnd();
    }
}
