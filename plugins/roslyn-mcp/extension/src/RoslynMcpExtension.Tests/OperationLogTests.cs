using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Newtonsoft.Json.Linq;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using StreamJsonRpc;
using Xunit;

namespace RoslynMcpExtension.Tests;

public sealed class OperationLogTests : IDisposable
{
	private static readonly string Folder = Path.Combine(Path.GetTempPath(), "roslyn-mcp-tests");

	private readonly TestWorkspace _workspace = new();
	private readonly OperationLog _log;

	public OperationLogTests()
	{
		_workspace.Workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Create(),
		                                                     filePath: Path.Combine(Folder, "Tests.sln")));
		_log = new OperationLog(_workspace.Workspace);
	}

	[Fact]
	public void DurationAndToolStandInColumns()
	{
		Assert.Equal("    95 ms  FindReferencesAsync      Target.Run(int) → 28 refs",
		             OperationLog.Line(95, "FindReferencesAsync", "Target.Run(int) → 28 refs"));
		Assert.Equal(" 45012 ms  FindImplementationsAsync IRunner → 2 implementations",
		             OperationLog.Line(45012, "FindImplementationsAsync", "IRunner → 2 implementations"));
	}

	[Fact]
	public void TheResolvedSymbolIsTheSubjectAndTheRequestOnlyWhenThereIsNone()
	{
		var request = _log.Request(Path.Combine(Folder, "Runner.cs"), 10, 5, null);
		var found = new SymbolListResult { Symbol = new SymbolLocation { LogName = "Target.Run(int)" }, TotalCount = 1, ReturnedCount = 1 };
		var missing = new SymbolListResult { ErrorCode = ToolErrorCodes.InvalidArgument, ErrorMessage = "No symbol found at line 10, column 5" };

		Assert.Equal("Target.Run(int) → 1 ref", OperationLog.Found(found, request, "ref"));
		Assert.Equal(@"Runner.cs:10:5 FAILED invalid_argument: No symbol found at line 10, column 5", OperationLog.Found(missing, request, "ref"));
		Assert.Equal("M:Target.Run(System.Int32)", _log.Request(null, 0, 0, "M:Target.Run(System.Int32)"));
	}

	[Fact]
	public void AFailureStaysOnOneLine()
	{
		Assert.Equal("Runner.cs:1:1 FAILED exception: IOException: Pipe is broken. Retry later.",
		             OperationLog.Failed("Runner.cs:1:1", "exception", "IOException: Pipe is broken.\r\nRetry later."));
	}

	[Fact]
	public void ATruncatedListSaysHowMuchItShows()
	{
		var references = new SymbolListResult { TotalCount = 2140, ReturnedCount = 500, Truncated = true };
		var callers = new SymbolListResult { TotalCount = 50, ReturnedCount = 50, Truncated = true };

		Assert.Equal("Run → 500 of 2140 refs (truncated)", OperationLog.Found(references, "Run", "ref"));
		Assert.Equal("Run → 50 call sites (truncated)", OperationLog.Found(callers, "Run", "call site"));
		Assert.Equal("\"Run\" → 0 matches", OperationLog.Found(new SymbolListResult(), "\"Run\"", "match", "matches"));
	}

	[Fact]
	public void PathsAreRelativeToTheSolutionFolder()
	{
		Assert.Equal(@"src\Runner.cs", _log.RelativePath(Path.Combine(Folder, "src", "Runner.cs")));
		Assert.Equal(@"src\Runner.cs", _log.RelativePath(Path.Combine(Folder, "src", "Runner.cs").Replace('\\', '/')));
		Assert.Equal(@"D:\elsewhere\Runner.cs", _log.RelativePath(@"D:\elsewhere\Runner.cs"));
		Assert.Equal("2 files", _log.Scope([@"D:\a.cs", @"D:\b.cs"], null));
		Assert.Equal("project Runner", _log.Scope(null, "Runner"));
	}

	[Fact]
	public void ADefinitionNamesWhereItIs()
	{
		var partial = new SymbolListResult
		{
			Symbol = new SymbolLocation { LogName = "Target" },
			Members = [new SymbolLocation { MemberType = "definition", FilePath = Path.Combine(Folder, "Target.cs"), StartLine = 3 },
			           new SymbolLocation { MemberType = "definition", FilePath = Path.Combine(Folder, "Target.Part.cs"), StartLine = 1 }]
		};
		var metadata = new SymbolListResult
		{
			Symbol = new SymbolLocation { LogName = "Console.WriteLine(string)", Assembly = "System.Console 8.0.0.0" },
			Members = [new SymbolLocation { MemberType = "metadata", FilePath = "System.Console" }]
		};

		Assert.Equal("Target → Target.cs:3 +1", _log.Definition(partial, "Runner.cs:1:1"));
		Assert.Equal("Console.WriteLine(string) → metadata System.Console 8.0.0.0", _log.Definition(metadata, "Runner.cs:1:1"));
	}

	[Fact]
	public void DiagnosticsNameTheProjectsTheyDidNotReach()
	{
		var result = new DiagnosticsResult { ErrorCount = 1, WarningCount = 4, CheckedProjects = ["A", "B"], UncheckedProjects = ["C.Tests"] };

		Assert.Equal("2 files → 1 error, 4 warnings in 2 projects; unchecked: C.Tests", OperationLog.Diagnostics(result, "2 files", true));
		Assert.Equal("2 files → 1 error in 2 projects; unchecked: C.Tests", OperationLog.Diagnostics(result, "2 files", false));
	}

	[Fact]
	public async Task AResolvedSymbolIsNamedInShortForm()
	{
		_workspace.AddProject("Definition", "class Target { void Run(int count) { Run(count); } }");

		var result = await new GoToDefinitionService(_workspace.Finder).GoToDefinitionAsync(TestWorkspace.PathOf("Definition"), 1, 38);

		Assert.Equal("Target.Run(int) → Definition.cs:1", _log.Definition(result, "Definition.cs:1:38"));
	}

	[Fact]
	public void LogNameStaysOutOfTheRpcPayload()
	{
		// The formatter SolutionPipe's `new JsonRpc(connected)` sends results with.
		var json = JObject.FromObject(new SymbolLocation { Name = "Run", LogName = "Target.Run(int)" }, new JsonMessageFormatter().JsonSerializer);

		Assert.Equal("Run", (string?)json["Name"]);
		Assert.Null(json["LogName"]);
	}

	public void Dispose() => _workspace.Dispose();
}
