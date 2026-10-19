using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using PlanForge.Acts;

namespace PlanForge.Evidence;

/// <summary>The local resolution and window fingerprint of one repository locator.</summary>
/// <param name="Resolved">Whether the locator identifies a line in a readable repository file.</param>
/// <param name="Window">The normalized window hash when resolved.</param>
/// <param name="Reason">The first failed check when unresolved.</param>
internal sealed record LocatorCheck([property: JsonRequired] bool Resolved,
                                    [property: JsonRequired] string? Window,
                                    [property: JsonRequired] string? Reason);

/// <summary>Resolves repository locators locally without exposing their source windows.</summary>
internal static class LocatorWindow
{
    /// <summary>The path cannot be interpreted by the filesystem.</summary>
    private const string INVALID_PATH = "invalid_path";
    /// <summary>The path is not relative to the workspace.</summary>
    private const string OUTSIDE_WORKSPACE = "outside_workspace";
    /// <summary>The symbol has no searchable final segment.</summary>
    private const string INVALID_SYMBOL = "invalid_symbol";
    /// <summary>The file does not exist.</summary>
    private const string MISSING_FILE = "missing_file";
    /// <summary>The file cannot be read within the size limit.</summary>
    private const string UNREADABLE = "unreadable";
    /// <summary>The requested line does not exist.</summary>
    private const string LINE_OUT_OF_RANGE = "line_out_of_range";
    /// <summary>No line contains the symbol as a whole word.</summary>
    private const string SYMBOL_NOT_FOUND = "symbol_not_found";
    /// <summary>The maximum number of UTF-8 bytes read from a source file.</summary>
    private const int MAX_FILE_BYTES = 4 * 1024 * 1024;

    /// <summary>Recognizes exactly the unresolved reasons produced by local locator checks.</summary>
    /// <param name="reason">The recorded failure reason.</param>
    public static bool IsReason(string? reason) => reason is INVALID_PATH or OUTSIDE_WORKSPACE or INVALID_SYMBOL
                                                           or MISSING_FILE or UNREADABLE or LINE_OUT_OF_RANGE or SYMBOL_NOT_FOUND;

    /// <summary>Checks a source already accepted by ScoutItem.IsRepositoryLocator.</summary>
    /// <param name="workspaceRoot">The Run's absolute repository root.</param>
    /// <param name="source">A repository path followed by a positive line or a symbol.</param>
    public static LocatorCheck Check(string workspaceRoot, string source)
    {
        var separator = source.LastIndexOf(':');
        var isLine = separator > 0
                     && int.TryParse(source[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture,
                                     out var parsedLine) && parsedLine > 0;
        if (!isLine) separator = source.LastIndexOf('#');
        var path = source[..separator];
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return Failed(INVALID_PATH);

        string fullPath;
        string root;
        try
        {
            path = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            root = Path.GetFullPath(workspaceRoot);
            if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
            fullPath = Path.GetFullPath(Path.Combine(root, path));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            return Failed(INVALID_PATH);
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.IsPathRooted(path) || !fullPath.StartsWith(root, comparison))
            return Failed(OUTSIDE_WORKSPACE);

        var symbol = source[(separator + 1)..];
        if (!isLine)
        {
            var cut = symbol.IndexOfAny(['(', '<']);
            if (cut >= 0) symbol = symbol[..cut];
            symbol = symbol[(symbol.LastIndexOf('.') + 1)..];
            if (string.IsNullOrWhiteSpace(symbol)) return Failed(INVALID_SYMBOL);
        }

        string text;
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MAX_FILE_BYTES) return Failed(UNREADABLE);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            text = reader.ReadToEnd();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failed(MISSING_FILE);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Failed(UNREADABLE);
        }

        if (text.StartsWith('\uFEFF')) text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = text.Length == 0 ? [] : text.Split('\n');
        var count = lines.Length;
        if (text.EndsWith('\n')) count--;
        var centre = isLine
            ? int.Parse(source[(separator + 1)..], CultureInfo.InvariantCulture) - 1
            : Array.FindIndex(lines, 0, count, line => ContainsWord(line, symbol));
        if (centre < 0 || centre >= count) return Failed(isLine ? LINE_OUT_OF_RANGE : SYMBOL_NOT_FOUND);

        var start = Math.Max(0, centre - 5);
        var end = Math.Min(count, centre + 6);
        return new LocatorCheck(true, PlanOutline.Hash(string.Join("\n", lines, start, end - start)), null);
    }

    /// <summary>Finds an ordinal symbol occurrence bounded by non-identifier characters.</summary>
    /// <param name="line">The normalized source line.</param>
    /// <param name="symbol">The non-empty final symbol segment.</param>
    private static bool ContainsWord(string line, string symbol)
    {
        for (var start = 0; start <= line.Length - symbol.Length;)
        {
            var index = line.IndexOf(symbol, start, StringComparison.Ordinal);
            if (index < 0) return false;
            var end = index + symbol.Length;
            if ((index == 0 || !IsIdentifierCharacter(line[index - 1]))
                && (end == line.Length || !IsIdentifierCharacter(line[end])))
                return true;
            start = index + 1;
        }
        return false;
    }

    /// <summary>Recognizes the characters that prevent a whole-word boundary.</summary>
    /// <param name="character">The character beside a potential symbol match.</param>
    private static bool IsIdentifierCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    /// <summary>Builds an unresolved check without a window.</summary>
    /// <param name="reason">The first failed check's reason code.</param>
    private static LocatorCheck Failed(string reason) => new(false, null, reason);
}
