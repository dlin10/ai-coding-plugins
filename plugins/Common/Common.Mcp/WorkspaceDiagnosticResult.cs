namespace Common.Mcp;

/// <summary>One diagnostic, or one fragment of one whose message is too long to travel whole: the
/// fragments of a message share an <paramref name="Id"/> and are numbered <paramref name="Part"/> of
/// <paramref name="Parts"/>, so a reader that pages to the end can put the message back together.</summary>
public sealed record WorkspaceDiagnosticResult(string Id, string Kind, string Message, int Part, int Parts);
