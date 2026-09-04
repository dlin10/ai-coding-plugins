using System.Text;

namespace PlanForge.Vendors.Codex;

/// <summary>
/// Codex takes role prompt, effort and sandbox mode as `-c key=value` overrides whose value is
/// parsed as TOML, so anything with a quote, a backslash or a newline has to be encoded rather than
/// pasted onto the command line.
/// </summary>
internal static class TomlValue
{
    private const char DELETE = (char)0x7f;

    internal static string String(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ' || c == DELETE)
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
