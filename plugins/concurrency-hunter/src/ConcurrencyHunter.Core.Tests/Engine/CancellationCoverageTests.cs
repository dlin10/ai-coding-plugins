using ConcurrencyHunter.Core.Tests.Fixtures;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class CancellationCoverageTests
{
    // These operations run over the completed result, outside cancellable work.
    private static readonly (string Path, string Reason)[] ExcludedPaths =
    [
        ("ConcurrencyHunter.Analysis/Narrative/", "Narratives describe the completed result."),
        ("ConcurrencyHunter.Analysis/Reporting/", "Reports render the completed result."),
        ("ConcurrencyHunter.Core/Analysis/Z3ConstraintSolver.cs", "Pair solving runs outside work.")
    ];

    private static readonly (string Type, string? Method, string Reason)[] ExcludedMembers =
    [
        ("InterproceduralPairing", null, "Access pairing runs outside work."),
        ("CandidateIndex", null, "The candidate index belongs to access pairing."),
        ("PhaseOneAnalyzer", "PairWithGaps", "Access pairing runs outside work.")
    ];

    private static string SourceRoot => RepositoryFiles.FindRepositoryFile("plugins", "concurrency-hunter", "src");

    [Fact]
    public void Analysis_code_checks_cancellation() => CheckArea(path => path.StartsWith("ConcurrencyHunter.Analysis/", StringComparison.Ordinal));

    [Fact]
    public void Frontend_code_checks_cancellation() => CheckArea(path => path.StartsWith("ConcurrencyHunter.Core/Frontend/", StringComparison.Ordinal));

    [Fact]
    public void Generation_code_checks_cancellation() => CheckArea(path => path.StartsWith("ConcurrencyHunter.Core/Providers/LibraryModels/Generation/", StringComparison.Ordinal));

    [Fact]
    public void Core_code_checks_cancellation() => CheckArea(path => path.StartsWith("ConcurrencyHunter.Core/", StringComparison.Ordinal)
        && !path.StartsWith("ConcurrencyHunter.Core/Frontend/", StringComparison.Ordinal)
        && !path.StartsWith("ConcurrencyHunter.Core/Providers/LibraryModels/Generation/", StringComparison.Ordinal));

    private static void CheckArea(Func<string, bool> includes)
    {
        var failures = Sources().Where(source => includes(source.Path))
                                .SelectMany(source => Violations(source.Root).Select(failure => Location(source.Path, failure.Node) + ": " + failure.Reason));
        AssertNoFailures(failures);
    }

    private static void AssertNoFailures(IEnumerable<string> failures)
    {
        var messages = failures.ToArray();
        if (messages.Length != 0)
            Assert.Fail(string.Join(Environment.NewLine, messages));
    }

    [Theory]
    [MemberData(nameof(RecognizerCases))]
    public void Recognizer_classifies_each_form(string source, string expected)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        Assert.DoesNotContain(root.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var failures = Violations(root).ToArray();
        if (expected.Length == 0)
            Assert.Empty(failures);
        else
            Assert.Contains(failures, failure => failure.Reason == expected);
    }

    public static IEnumerable<object[]> RecognizerCases()
    {
        string[] loops = ["for (;;) BODY", "foreach (var item in items) BODY", "while (true) BODY", "do BODY while (true);"];
        string[] checks = ["WorkCancellation.Check();", "CheckCancellation();", "this.CheckCancellation();",
                           "checks.Check();", "_checks.Check(1);", "Checks.Check();", "this._checks.Check();", "owner.Checks.Check();"];
        foreach (var loop in loops)
        {
            foreach (var check in checks)
                yield return Case(Method(loop.Replace("BODY", "{ " + check + " Step(); }")), "");
            yield return Case(Method("// bounded: two fixed entries\n" + loop.Replace("BODY", "{ Step(); }")), "");
            foreach (var wrong in new[] { "", "Step(); WorkCancellation.Check();", "Check();", "token.ThrowIfCancellationRequested();",
                                          "other.Check();", "other.Each(items);" })
                yield return Case(Method(loop.Replace("BODY", "{ " + wrong + " }")), "missing cancellation check");
            yield return Case(Method(loop.Replace("BODY", "WorkCancellation.Check();")), "body is not a block");
        }

        foreach (var receiver in new[] { "checks", "_checks", "Checks", "this.checks", "owner._checks" })
            yield return Case(Method("foreach (var x in " + receiver + ".Each(items)) { Step(); }"), "");
        yield return Case(Method("foreach (var x in other.Each(items)) { Step(); }"), "missing cancellation check");
        yield return Case(Method("foreach (var x in _checks.Each(items)) Step();"), "body is not a block");

        foreach (var context in new[] { "finally", "Dispose", "DisposeAsync", "cleanup" })
        {
            string Wrap(string body) => context switch
            {
                "finally" => Method("try { Step(); } finally { " + body + " }"),
                "cleanup" => "class C {\n// cleanup: releases owned handles\nvoid Release() { " + body + " } }",
                _ => "class C { void " + context + "() { " + body + " } }"
            };
            yield return Case(Wrap("while (true) { Step(); }"), "");
            foreach (var check in checks.Concat(["token.ThrowIfCancellationRequested();", "other.CheckCancellation();"]))
            {
                yield return Case(Wrap("while (true) { " + check + " }"), "check in cleanup");
                yield return Case(Wrap("while (true) { if (flag) { " + check + " } }"), "check in cleanup");
            }
            yield return Case(Wrap("foreach (var x in _checks.Each(items)) { Step(); }"), "check in cleanup");
        }
        yield return Case(Method("while (true) { Step(); }"), "missing cancellation check");

        foreach (var call in new[] { "Walk()", "this.Walk()", "C.Walk()", "next.Walk()", "((C)this).Walk()",
                                     "next?.Walk()", "Walk<int>()", "this.Walk<int>()", "C.Walk<int>()",
                                     "next.Walk<int>()", "((C)this).Walk<int>()", "next?.Walk<int>()", "next.Walk(1)" })
        {
            yield return Case("class C { void Walk() { " + call + "; } }", "missing cancellation check");
            yield return Case("class C { void Walk() { WorkCancellation.Check(); " + call + "; } }", "");
            yield return Case("class C {\n// bounded: the two overloads forward once\nvoid Walk() { " + call + "; } }", "");
            yield return Case(Method("void Walk() { " + call + "; }"), "missing cancellation check");
            yield return Case(Method("void Walk() { CheckCancellation(); " + call + "; }"), "");
        }
        yield return Case("class C { void Walk() { base.Walk(); } }", "");
        yield return Case("class C { void Walk() { base.Walk<int>(); } }", "");
        yield return Case(Method("void Walk() { base.Walk(); }"), "");
        yield return Case("class C { void Walk() => Walk(); }", "body is not a block");
        yield return Case(Method("void Walk() => Walk();"), "body is not a block");
        yield return Case(Method("// bounded: forwards once to the other overload\nvoid Walk() => Walk(1);"), "");
        yield return Case("class C { void Walk() { Step(); WorkCancellation.Check(); Walk(); } }", "missing cancellation check");
        yield return Case("class C { void Walk() { token.ThrowIfCancellationRequested(); Walk(); } }", "missing cancellation check");
        yield return Case("class C { void Walk() { Other(); } void Other() { Walk(); } }", "");
        yield return Case(Method("items.Select(x => x);"), "");
        yield return Case(Method("// bounded:\nwhile (true) { Step(); }"), "missing cancellation check");
        yield return Case(Method("// bounded: two entries\n\nwhile (true) { Step(); }"), "missing cancellation check");
        yield return Case("class C {\n// cleanup:\nvoid Release() { while (true) { Step(); } } }", "missing cancellation check");
        yield return Case("class InterproceduralPairing { void Walk() { while (true) { Walk(); } } }", "");
        yield return Case("class CandidateIndex { void Walk() { while (true) { Walk(); } } }", "");
        yield return Case("class PhaseOneAnalyzer { void PairWithGaps() { while (true) { Step(); } } }", "");
        yield return Case("class C { void PairWithGaps() { while (true) { Step(); } } }", "missing cancellation check");
        yield return Case(Method("// bounded: two entries\ntry { Step(); } finally { while (true) { WorkCancellation.Check(); } }"), "check in cleanup");
    }

    private static object[] Case(string source, string expected) => [source, expected];
    private static string Method(string body) => "class C { void Run() {\n" + body + "\n} }";

    [Fact]
    public void Bounded_marker_names_a_reason()
    {
        foreach (var marker in new[] { "bounded", "cleanup" })
        {
            Assert.False(ValidMarker("// " + marker + ":", marker));
            Assert.False(ValidMarker("// " + marker + ":   ", marker));
            Assert.True(ValidMarker("// " + marker + ": two entries", marker));
        }
        AssertNoFailures(Sources().SelectMany(source => source.Root.DescendantTrivia()
            .Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                && new[] { "bounded", "cleanup" }.Any(marker => IsMarker(trivia.ToString(), marker) && !ValidMarker(trivia.ToString(), marker)))
            .Select(trivia => source.Path + ":" + (trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1) + ": marker needs a reason")));
    }

    [Fact]
    public void Check_helpers_reach_the_work_cancellation()
    {
        AssertNoFailures(Sources().SelectMany(source => source.Root.DescendantNodes()
            .Where(node => !Excluded(node) && MethodName(node) == "CheckCancellation")
            .Where(node => !node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsWorkCheck))
            .Select(node => Location(source.Path, node) + ": CheckCancellation must call WorkCancellation.Check()")));
    }

    [Fact]
    public void Checks_receivers_are_stage_checks()
    {
        AssertNoFailures(Sources().SelectMany(source => source.Root.DescendantNodes().Where(node => !Excluded(node))
            .Select(node => (Node: node, Declaration: CheckDeclaration(node)))
            .Where(item => item.Declaration is { } declaration && IsChecksName(declaration.Name)
                && declaration.Type is not ("StageChecks" or "StageChecks?" or "GenerationChecks" or "GenerationChecks?"))
            .Select(item => Location(source.Path, item.Node) + ": " + item.Declaration!.Value.Name + " must explicitly name the checks type")));
    }

    private static (string Name, string? Type)? CheckDeclaration(SyntaxNode node) => node switch
    {
        VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } variable => (variable.Identifier.ValueText, declaration.Type.ToString()),
        PropertyDeclarationSyntax property => (property.Identifier.ValueText, property.Type.ToString()),
        ParameterSyntax parameter => (parameter.Identifier.ValueText, parameter.Type?.ToString()),
        ForEachStatementSyntax loop => (loop.Identifier.ValueText, loop.Type.ToString()),
        CatchDeclarationSyntax declaration => (declaration.Identifier.ValueText, declaration.Type.ToString()),
        SingleVariableDesignationSyntax variable => (variable.Identifier.ValueText, DesignationType(variable)),
        _ => null
    };

    private static string? DesignationType(SingleVariableDesignationSyntax variable) => variable.Parent switch
    {
        DeclarationExpressionSyntax declaration => declaration.Type.ToString(),
        DeclarationPatternSyntax pattern => pattern.Type.ToString(),
        RecursivePatternSyntax pattern => pattern.Type?.ToString(),
        _ => null
    };

    [Fact]
    public void Excluded_code_exists()
    {
        foreach (var (path, reason) in ExcludedPaths)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.True(File.Exists(Path.Combine(SourceRoot, path)) || Directory.Exists(Path.Combine(SourceRoot, path)), path);
        }
        var roots = Sources(includeExcluded: true).Select(source => source.Root).ToArray();
        foreach (var (type, method, reason) in ExcludedMembers)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
            var declarations = roots.SelectMany(root => root.DescendantNodes().OfType<TypeDeclarationSyntax>()).Where(node => node.Identifier.ValueText == type).ToArray();
            Assert.NotEmpty(declarations);
            if (method is not null)
                Assert.Contains(declarations.SelectMany(node => node.Members).OfType<MethodDeclarationSyntax>(), node => node.Identifier.ValueText == method);
        }
    }

    private static IEnumerable<(string Path, SyntaxNode Root)> Sources(bool includeExcluded = false)
    {
        foreach (var project in new[] { "ConcurrencyHunter.Analysis", "ConcurrencyHunter.Core" })
        foreach (var file in Directory.EnumerateFiles(Path.Combine(SourceRoot, project), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var path = Path.GetRelativePath(SourceRoot, file).Replace('\\', '/');
            if (path.Split('/').Any(part => part is "bin" or "obj") || !includeExcluded && ExcludedPaths.Any(item => item.Path.EndsWith('/')
                ? path.StartsWith(item.Path, StringComparison.Ordinal) : path == item.Path))
                continue;
            yield return (path, CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file).GetRoot());
        }
    }

    private static string Location(string path, SyntaxNode node) => path + ":" + (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static IEnumerable<(SyntaxNode Node, string Reason)> Violations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes().Where(node => !Excluded(node)))
        {
            if (LoopBody(node) is { } body)
            {
                if (InCleanup(node))
                {
                    if (body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(IsCleanupCheck) || EachChecked(node))
                        yield return (node, "check in cleanup");
                }
                else if (!HasMarker(node, "bounded"))
                {
                    if (body is not BlockSyntax)
                        yield return (node, "body is not a block");
                    else if (!FirstChecked(body) && !EachChecked(node))
                        yield return (node, "missing cancellation check");
                }
            }
            else if (MethodName(node) is { } name && MethodBody(node) is { } methodBody
                && methodBody.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call => CalledName(call) == name && !BaseCall(call))
                && !HasMarker(node, "bounded"))
            {
                if (methodBody is not BlockSyntax)
                    yield return (node, "body is not a block");
                else if (!FirstChecked(methodBody))
                    yield return (node, "missing cancellation check");
            }
        }
    }

    private static bool Excluded(SyntaxNode node) => node.AncestorsAndSelf().Any(ancestor => ExcludedMembers.Any(item =>
        ancestor is TypeDeclarationSyntax type && type.Identifier.ValueText == item.Type && item.Method is null
        || ancestor is MethodDeclarationSyntax method && method.Identifier.ValueText == item.Method
            && method.Parent is TypeDeclarationSyntax declaring && declaring.Identifier.ValueText == item.Type));

    private static StatementSyntax? LoopBody(SyntaxNode node) => node switch
    {
        ForStatementSyntax loop => loop.Statement,
        CommonForEachStatementSyntax loop => loop.Statement,
        WhileStatementSyntax loop => loop.Statement,
        DoStatementSyntax loop => loop.Statement,
        _ => null
    };

    private static string? MethodName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => method.Identifier.ValueText,
        LocalFunctionStatementSyntax method => method.Identifier.ValueText,
        _ => null
    };

    private static SyntaxNode? MethodBody(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
        LocalFunctionStatementSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
        _ => null
    };

    private static bool InCleanup(SyntaxNode node) => node.Ancestors().Any(ancestor => ancestor is FinallyClauseSyntax
        || MethodName(ancestor) is "Dispose" or "DisposeAsync" || MethodName(ancestor) is not null && HasMarker(ancestor, "cleanup"));

    private static bool FirstChecked(SyntaxNode body) => body is BlockSyntax block && block.Statements.FirstOrDefault() is ExpressionStatementSyntax
        { Expression: InvocationExpressionSyntax call } && IsCheck(call);

    private static bool IsWorkCheck(InvocationExpressionSyntax call) => call.ArgumentList.Arguments.Count == 0
        && call.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "WorkCancellation" }, Name.Identifier.ValueText: "Check" };

    private static bool IsCheck(InvocationExpressionSyntax call) => IsWorkCheck(call)
        || call.ArgumentList.Arguments.Count == 0 && (call.Expression is IdentifierNameSyntax { Identifier.ValueText: "CheckCancellation" }
            || call.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "CheckCancellation" })
        || call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Check" } member && ChecksReceiver(member.Expression);

    private static bool IsCleanupCheck(InvocationExpressionSyntax call) => IsCheck(call)
        || CalledName(call) == "CheckCancellation"
        || call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ThrowIfCancellationRequested" }
        || call.Expression is MemberBindingExpressionSyntax { Name.Identifier.ValueText: "ThrowIfCancellationRequested" };

    private static bool EachChecked(SyntaxNode node) => node is CommonForEachStatementSyntax
        { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Each" } member } }
        && ChecksReceiver(member.Expression);

    private static bool ChecksReceiver(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => IsChecksName(name.Identifier.ValueText),
        MemberAccessExpressionSyntax member => IsChecksName(member.Name.Identifier.ValueText),
        _ => false
    };

    private static bool IsChecksName(string name) => name is "checks" or "_checks" or "Checks";

    private static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    private static bool BaseCall(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax };

    private static bool HasMarker(SyntaxNode node, string marker)
    {
        var text = node.SyntaxTree.GetText();
        var line = text.Lines.GetLineFromPosition(node.SpanStart).LineNumber;
        return line > 0 && ValidMarker(text.Lines[line - 1].ToString().Trim(), marker);
    }

    private static bool IsMarker(string text, string marker) => text.StartsWith("// " + marker + ":", StringComparison.Ordinal);
    private static bool ValidMarker(string text, string marker) => IsMarker(text, marker) && !string.IsNullOrWhiteSpace(text[(marker.Length + 4)..]);
}
