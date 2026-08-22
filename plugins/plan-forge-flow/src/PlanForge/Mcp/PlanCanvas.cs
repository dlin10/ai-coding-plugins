using System.ComponentModel;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

namespace PlanForge.Mcp;

/// <summary>
/// The MCP Apps UI resource that <c>forge.plan.show</c> renders into. One static HTML template:
/// the host loads it once and pushes each tool result into it over the postMessage bridge, so the
/// plan itself never travels in this document.
/// </summary>
/// <remarks>
/// Self-contained on purpose. The host frames this in a sandbox whose CSP has no allowance for an
/// origin we did not declare, and declaring one would buy a markdown library at the cost of a
/// network dependency in the middle of the approval step. The renderer below is smaller than that
/// allowance would be worth.
/// </remarks>
[McpServerResourceType]
internal sealed class PlanCanvas
{
    internal const string ResourceUri = "ui://planforge/plan.html";

    [McpServerResource(UriTemplate = ResourceUri,
                       Name = "plan-canvas",
                       Title = "Plan Forge plan",
                       MimeType = McpApps.HtmlMimeType)]
    [Description("The document view of a plan awaiting approval, with the working-tree drift beside it.")]
    public static string Plan() => Html;

    private const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <title>Plan</title>
        <style>
          :root {
            color-scheme: light;
            --bg: #ffffff;
            --fg: #1b1b1f;
            --muted: #5d6169;
            --rule: #e3e5ea;
            --card: #f6f7f9;
            --code-bg: #f0f1f4;
            --accent: #3b5bdb;
            --warn-bg: #fff6e5;
            --warn-fg: #7a4b00;
            --warn-rule: #f0c274;
          }
          html[data-theme="dark"] {
            color-scheme: dark;
            --bg: #16171a;
            --fg: #e8e9ed;
            --muted: #9ba1ac;
            --rule: #2c2e34;
            --card: #1e2024;
            --code-bg: #24262b;
            --accent: #91a7ff;
            --warn-bg: #2e2413;
            --warn-fg: #f0c274;
            --warn-rule: #6b5220;
          }
          @media (prefers-color-scheme: dark) {
            html:not([data-theme="light"]) {
              color-scheme: dark;
              --bg: #16171a;
              --fg: #e8e9ed;
              --muted: #9ba1ac;
              --rule: #2c2e34;
              --card: #1e2024;
              --code-bg: #24262b;
              --accent: #91a7ff;
              --warn-bg: #2e2413;
              --warn-fg: #f0c274;
              --warn-rule: #6b5220;
            }
          }
          * { box-sizing: border-box; }
          body {
            margin: 0;
            padding: 20px 22px 26px;
            background: var(--bg);
            color: var(--fg);
            font: 14px/1.6 ui-sans-serif, -apple-system, "Segoe UI", system-ui, sans-serif;
            overflow-wrap: break-word;
          }
          header { border-bottom: 1px solid var(--rule); padding-bottom: 12px; margin-bottom: 4px; }
          h1 { font-size: 17px; margin: 0 0 6px; letter-spacing: -0.01em; }
          .meta { color: var(--muted); font-size: 12px; display: flex; gap: 14px; flex-wrap: wrap; }
          .meta code { background: none; padding: 0; }
          .drift {
            background: var(--warn-bg);
            color: var(--warn-fg);
            border: 1px solid var(--warn-rule);
            border-radius: 8px;
            padding: 10px 14px;
            margin: 16px 0 0;
            font-size: 13px;
          }
          .drift strong { display: block; margin-bottom: 4px; }
          .drift ul { margin: 0; padding-left: 20px; }
          .clean { color: var(--muted); font-size: 12px; margin: 14px 0 0; }
          .plan { margin-top: 20px; }
          .plan h1, .plan h2, .plan h3, .plan h4 { line-height: 1.3; margin: 22px 0 8px; }
          .plan h1 { font-size: 16px; }
          .plan h2 { font-size: 15px; border-bottom: 1px solid var(--rule); padding-bottom: 5px; }
          .plan h3 { font-size: 14px; }
          .plan h4 { font-size: 13px; color: var(--muted); }
          .plan p { margin: 10px 0; }
          .plan ul, .plan ol { margin: 10px 0; padding-left: 24px; }
          .plan li { margin: 5px 0; }
          .plan hr { border: 0; border-top: 1px solid var(--rule); margin: 20px 0; }
          .plan a { color: var(--accent); }
          code {
            background: var(--code-bg);
            border-radius: 4px;
            padding: 1px 5px;
            font: 12.5px/1.5 ui-monospace, "Cascadia Code", Consolas, monospace;
          }
          pre {
            background: var(--card);
            border: 1px solid var(--rule);
            border-radius: 8px;
            padding: 12px 14px;
            overflow-x: auto;
          }
          pre code { background: none; padding: 0; }
          footer {
            margin-top: 26px;
            padding-top: 12px;
            border-top: 1px solid var(--rule);
            color: var(--muted);
            font-size: 12px;
          }
          .waiting { color: var(--muted); }
        </style>
        </head>
        <body>
        <div id="root"><p class="waiting">Waiting for the plan.</p></div>
        <script>
        (function () {
          var nextId = 1;
          var initId = 0;

          function send(message) {
            window.parent.postMessage(message, "*");
          }

          function request(method, params) {
            var id = nextId++;
            send({ jsonrpc: "2.0", id: id, method: method, params: params || {} });
            return id;
          }

          function notify(method, params) {
            send({ jsonrpc: "2.0", method: method, params: params || {} });
          }

          function escapeHtml(text) {
            return String(text)
              .replace(/&/g, "&amp;")
              .replace(/</g, "&lt;")
              .replace(/>/g, "&gt;")
              .replace(/"/g, "&quot;");
          }

          // Escaping runs first, so every tag below is one this function put there.
          function inline(text) {
            var out = escapeHtml(text);
            out = out.replace(/`([^`]+)`/g, "<code>$1</code>");
            out = out.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
            out = out.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
            out = out.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, '<a href="$2">$1</a>');
            return out;
          }

          function markdown(source) {
            var lines = String(source).replace(/\r\n?/g, "\n").split("\n");
            var html = [];
            var stack = [];
            var paragraph = [];
            var code = null;

            function flushParagraph() {
              if (!paragraph.length) return;
              html.push("<p>" + inline(paragraph.join(" ")) + "</p>");
              paragraph = [];
            }

            function closeLists(downTo) {
              while (stack.length && stack[stack.length - 1].indent >= downTo) {
                html.push("</" + stack.pop().tag + ">");
              }
            }

            function closeBlocks() {
              flushParagraph();
              closeLists(0);
            }

            for (var i = 0; i < lines.length; i++) {
              var line = lines[i];

              if (code !== null) {
                if (/^\s*```/.test(line)) {
                  html.push("<pre><code>" + escapeHtml(code.join("\n")) + "</code></pre>");
                  code = null;
                } else {
                  code.push(line);
                }
                continue;
              }

              if (/^\s*```/.test(line)) { closeBlocks(); code = []; continue; }
              if (!line.trim()) { flushParagraph(); continue; }

              var heading = /^(#{1,6})\s+(.*)$/.exec(line);
              if (heading) {
                closeBlocks();
                var level = Math.min(heading[1].length, 4);
                html.push("<h" + level + ">" + inline(heading[2]) + "</h" + level + ">");
                continue;
              }

              if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { closeBlocks(); html.push("<hr>"); continue; }

              var item = /^(\s*)(?:([-*+])|(\d+)[.)])\s+(.*)$/.exec(line);
              if (item) {
                flushParagraph();
                var indent = item[1].length;
                var tag = item[2] ? "ul" : "ol";
                while (stack.length && stack[stack.length - 1].indent > indent) {
                  html.push("</" + stack.pop().tag + ">");
                }
                var top = stack[stack.length - 1];
                if (top && top.indent === indent && top.tag !== tag) {
                  html.push("</" + stack.pop().tag + ">");
                  top = stack[stack.length - 1];
                }
                if (!top || top.indent < indent) {
                  // A nested list is emitted as a sibling of the item above it: browsers render
                  // that the way the markdown reads, and it costs no item-scoped bookkeeping.
                  html.push("<" + tag + ">");
                  stack.push({ tag: tag, indent: indent });
                }
                html.push("<li>" + inline(item[4]) + "</li>");
                continue;
              }

              closeLists(0);
              paragraph.push(line.trim());
            }

            if (code !== null) html.push("<pre><code>" + escapeHtml(code.join("\n")) + "</code></pre>");
            closeBlocks();
            return html.join("");
          }

          function driftBlock(files) {
            if (!files || !files.length) {
              return '<p class="clean">No working-tree drift since the run started.</p>';
            }
            var items = files.map(function (file) {
              return "<li><code>" + escapeHtml(file) + "</code></li>";
            }).join("");
            return '<div class="drift"><strong>' + files.length +
              (files.length === 1 ? " file has" : " files have") +
              " changed since this run started</strong><ul>" + items + "</ul></div>";
          }

          function render(data) {
            var meta = [];
            if (data.runId) meta.push("run <code>" + escapeHtml(data.runId) + "</code>");
            if (typeof data.reviewRounds === "number") {
              meta.push(data.reviewRounds + (data.reviewRounds === 1 ? " review round" : " review rounds"));
            }
            if (data.approved) meta.push("already approved");

            document.getElementById("root").innerHTML =
              "<header><h1>Plan awaiting your approval</h1>" +
              '<div class="meta">' + meta.join("<span>·</span>") + "</div></header>" +
              driftBlock(data.driftedFiles) +
              '<div class="plan">' + markdown(data.plan || "") + "</div>" +
              "<footer>Approve this plan, or say what to change, in the chat. " +
              "Nothing here records your answer.</footer>";

            reportSize();
          }

          function fromToolResult(params) {
            if (!params) return null;
            if (params.structuredContent) return params.structuredContent;
            var content = params.content || [];
            for (var i = 0; i < content.length; i++) {
              if (content[i] && content[i].type === "text") {
                try { return JSON.parse(content[i].text); } catch (error) { /* not ours to render */ }
              }
            }
            return null;
          }

          function applyContext(context) {
            if (context && (context.theme === "light" || context.theme === "dark")) {
              document.documentElement.setAttribute("data-theme", context.theme);
            }
          }

          function reportSize() {
            notify("ui/notifications/size-changed", {
              width: document.documentElement.scrollWidth,
              height: document.body.scrollHeight
            });
          }

          window.addEventListener("message", function (event) {
            var message = event.data;
            if (!message || message.jsonrpc !== "2.0") return;

            if (message.method === "ui/notifications/tool-result") {
              var data = fromToolResult(message.params);
              if (data) render(data);
              return;
            }
            if (message.method === "ui/notifications/host-context-changed") {
              applyContext(message.params);
              return;
            }
            if (message.id === initId && message.result) {
              applyContext(message.result.hostContext);
            }
          });

          // Links open through the host: the frame is sandboxed, so navigating it ourselves either
          // does nothing or replaces the plan with the target.
          document.addEventListener("click", function (event) {
            var anchor = event.target && event.target.closest && event.target.closest("a[href]");
            if (!anchor) return;
            event.preventDefault();
            request("ui/open-link", { url: anchor.getAttribute("href") });
          });

          if (window.ResizeObserver) new ResizeObserver(reportSize).observe(document.body);

          initId = request("ui/initialize", {
            protocolVersion: "2026-01-26",
            clientInfo: { name: "planforge-plan-canvas", version: "1" },
            capabilities: { appCapabilities: {} }
          });
        })();
        </script>
        </body>
        </html>
        """;
}
