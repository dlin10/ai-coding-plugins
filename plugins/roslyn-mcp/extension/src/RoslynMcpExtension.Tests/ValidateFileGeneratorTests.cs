using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;

namespace RoslynMcpExtension.Tests;

public sealed class ValidateFileGeneratorTests : IDisposable
{
	private readonly AdhocWorkspace _workspace = new();
	private static readonly List<object> Timings = [];
	private const string UsesGenerator = "class Consumer { int Read() => Generated.Value; }";

	private Document Document(string source, bool generated = false, string? other = null)
	{
		var project = _workspace.AddProject("Validation", LanguageNames.CSharp)
			.WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
			.AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
		if (generated) project = project.WithAnalyzerReferences([new GeneratorReference()]);
		if (other != null) project = project.AddDocument("Other.cs", other, filePath: Path.Combine(Path.GetTempPath(), "Other.cs")).Project;
		return project.AddDocument("Consumer.cs", source, filePath: Path.Combine(Path.GetTempPath(), "Consumer.cs"));
	}

	[Fact]
	public async Task GeneratedMembersValidate()
	{
		var doc = Document(UsesGenerator, generated: true);
		var result = await ValidateFileService.ValidateDocumentAsync(doc, true, false);
		Assert.True(result.Success);
		Assert.Equal(1, result.SourceGeneratedDocumentCount);
		var withoutGenerator = doc.Project.WithAnalyzerReferences([]).GetDocument(doc.Id)!;
		Assert.False((await ValidateFileService.ValidateDocumentAsync(withoutGenerator, true, false)).Success);
	}

	[Fact]
	public async Task RealErrorsAlongsideGeneratedMembersSurvive()
	{
		var result = await ValidateFileService.ValidateDocumentAsync(Document(UsesGenerator + " class Broken { int X => Missing; }", true), true, false);
		Assert.False(result.Success);
		Assert.Contains(result.Errors, d => d.Id == "CS0103" && d.Message.Contains("Missing"));
		Assert.Equal(1, result.SourceGeneratedDocumentCount);
	}

	[Fact]
	public async Task NoGeneratorReturnsKnownZero()
	{
		var result = await ValidateFileService.ValidateDocumentAsync(Document("class Fine {}"), true, false);
		Assert.True(result.Success);
		Assert.Equal(0, result.SourceGeneratedDocumentCount);
	}

	[Fact]
	public async Task WarningsFollowIncludeWarnings()
	{
		var doc = Document("#warning issue70\nclass Fine {}");
		Assert.Contains((await ValidateFileService.ValidateDocumentAsync(doc, true, false)).Warnings, d => d.Id == "CS1030");
		Assert.Empty((await ValidateFileService.ValidateDocumentAsync(doc, false, false)).Warnings);
	}

	[Fact]
	public async Task DiagnosticsExcludeOtherFiles()
	{
		var result = await ValidateFileService.ValidateDocumentAsync(Document("class Fine {}", other: "class Broken { int X => Missing; }"), true, false);
		Assert.True(result.Success);
		Assert.Empty(result.Errors);
	}

	[Fact]
	public async Task AnalyzerOptionPreservesDeduplication()
	{
		var result = await ValidateFileService.ValidateDocumentAsync(Document("class Broken { int X => Missing; }"), true, true);
		Assert.Single(result.Errors);
		Assert.Empty(result.AnalyzerDiagnostics);
	}

	[Fact]
	public void ResultFinalizationDistinguishesRequestAndCompilerFailures()
	{
		var success = new ValidateFileResult { Success = true };
		var compiler = new ValidateFileResult { Success = false };
		var request = new ValidateFileResult { ErrorMessage = "Not found", ErrorCode = ToolErrorCodes.DocumentNotFound };
		foreach (var result in new[] { success, compiler, request }) RoslynAnalysisService.CompleteResult(result);
		Assert.True(success.RequestSucceeded && success.Success);
		Assert.True(compiler.RequestSucceeded);
		Assert.False(compiler.Success);
		Assert.False(request.RequestSucceeded || request.Success);
		Assert.Equal(ToolErrorCodes.DocumentNotFound, request.ErrorCode);
		var unspecified = new ValidateFileResult { ErrorMessage = "Failed" };
		RoslynAnalysisService.CompleteResult(unspecified);
		Assert.Equal(ToolErrorCodes.InternalError, unspecified.ErrorCode);
	}

	[Fact]
	public async Task OptionalFaultRetainsBaselineDiagnostics()
	{
		var doc = Document("#warning issue70\nclass Broken { int X => Missing; }");
		var baseline = await ValidateFileService.ValidateDocumentAsync(doc, true, false);
		var watch = Stopwatch.StartNew();
		var result = await ValidateFileService.ValidateDocumentAsync(doc, true, false, TimeSpan.FromMilliseconds(50), _ => throw new InvalidOperationException("optional"));
		Record("fault", watch.Elapsed.TotalMilliseconds, result);
		Assert.Equal(baseline.Errors.Select(d => d.Message), result.Errors.Select(d => d.Message));
		Assert.Equal(baseline.Warnings.Select(d => d.Message), result.Warnings.Select(d => d.Message));
		Assert.Null(result.SourceGeneratedDocumentCount);
		Assert.Null(result.ErrorMessage);
	}

	[Fact]
	public async Task TimeoutCancelsAndIgnoresLateResult()
	{
		var doc = Document("class Broken { int X => Missing; }");
		var baselineWatch = Stopwatch.StartNew();
		var baseline = await ValidateFileService.ValidateDocumentAsync(doc, true, false);
		Record("baseline", baselineWatch.Elapsed.TotalMilliseconds, baseline);
		var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var watch = Stopwatch.StartNew();
		var validation = ValidateFileService.ValidateDocumentAsync(doc, true, false, TimeSpan.FromMilliseconds(100), token =>
		{
			token.Register(() => cancelled.TrySetResult(true));
			return pending.Task;
		});
		Assert.Same(validation, await Task.WhenAny(validation, Task.Delay(5000)));
		var result = await validation;
		Record("timeout", watch.Elapsed.TotalMilliseconds, result);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "100ms optional budget plus 4900ms scheduler/validation tolerance on a loaded test host");
		Assert.Same(cancelled.Task, await Task.WhenAny(cancelled.Task, Task.Delay(2000)));
		Assert.Contains(result.Errors, d => d.Id == "CS0103");
		Assert.Null(result.SourceGeneratedDocumentCount);
		pending.SetResult(37);
		await pending.Task;
		Assert.Null(result.SourceGeneratedDocumentCount);
	}

	[Fact]
	public async Task TimeoutDoesNotWaitForBlockingCancellationCallback()
	{
		using var releaseCallback = new ManualResetEventSlim();
		var callbackEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var callbackExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		var observation = OptionalGeneratorObservation.RunAsync(token =>
		{
			token.Register(() =>
			{
				callbackEntered.TrySetResult(true);
				try { releaseCallback.Wait(); }
				finally { callbackExited.TrySetResult(true); }
				throw new InvalidOperationException("cancellation callback");
			});
			return pending.Task;
		}, TimeSpan.FromMilliseconds(100));

		try
		{
			Assert.Same(callbackEntered.Task, await Task.WhenAny(callbackEntered.Task, Task.Delay(5000)));
			Assert.Same(observation, await Task.WhenAny(observation, Task.Delay(5000)));
			Assert.Null(await observation);
			Assert.False(callbackExited.Task.IsCompleted);
		}
		finally
		{
			releaseCallback.Set();
			pending.TrySetException(new InvalidOperationException("late observation failure"));
			Assert.Same(callbackExited.Task, await Task.WhenAny(callbackExited.Task, Task.Delay(5000)));
		}
	}

	[Fact]
	public async Task CompletedObservationReturnsCountWithoutChangingErrors()
	{
		var doc = Document("class Broken { int X => Missing; }");
		var result = await ValidateFileService.ValidateDocumentAsync(doc, true, false, TimeSpan.FromSeconds(1), _ => Task.FromResult(7));
		Assert.Equal(7, result.SourceGeneratedDocumentCount);
		Assert.False(result.Success);
		Assert.Contains(result.Errors, d => d.Id == "CS0103");
	}

	[Fact]
	public void ProductionBudgetIsTenSeconds()
	{
		var field = typeof(ValidateFileService).GetField("OptionalBudgetSeconds", BindingFlags.Static | BindingFlags.NonPublic)!;
		Assert.True(field.IsLiteral && field.IsPrivate);
		Assert.Equal(10, field.GetRawConstantValue());
	}

	[Fact]
	public void ProductionObservationIsStructurallyInsideBudgetHelper()
	{
		var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
		while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Services", "ValidateFileService.cs")) &&
			!File.Exists(Path.Combine(directory.FullName, "RoslynMcpExtension", "Services", "ValidateFileService.cs"))) directory = directory.Parent;
		Assert.NotNull(directory);
		var path = Path.Combine(directory!.FullName, "RoslynMcpExtension", "Services", "ValidateFileService.cs");
		var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetRoot();
		var calls = syntax.DescendantNodes().OfType<InvocationExpressionSyntax>().ToArray();
		string Name(InvocationExpressionSyntax call) => call.Expression is MemberAccessExpressionSyntax member
			? member.Name.Identifier.ValueText : (call.Expression as IdentifierNameSyntax)?.Identifier.ValueText ?? "";
		var compilation = Assert.Single(calls.Where(call => Name(call) == "GetCompilationAsync"));
		Assert.Equal("ValidateDocumentAsync", compilation.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText);
		var generated = Assert.Single(calls.Where(call => Name(call) == "GetSourceGeneratedDocumentsAsync"));
		var lambda = generated.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
		Assert.NotNull(lambda);
		var helper = Assert.Single(calls.Where(call => call.Expression is MemberAccessExpressionSyntax member &&
			Name(call) == "RunAsync" && member.Expression.ToString() == nameof(OptionalGeneratorObservation)));
		var argument = helper.ArgumentList.Arguments[0];
		var local = lambda!.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
		if (local == null) Assert.Contains(lambda, argument.DescendantNodes());
		else Assert.Contains(argument.DescendantNodes().OfType<IdentifierNameSyntax>(), id => id.Identifier.ValueText == local.Identifier.ValueText);
		var method = generated.Ancestors().OfType<MethodDeclarationSyntax>().First();
		var delegates = method.ParameterList.Parameters.Where(p => (p.Type is NullableTypeSyntax nullable ? nullable.ElementType : p.Type) is GenericNameSyntax generic && generic.Identifier.ValueText == "Func")
			.Select(p => p.Identifier.ValueText).Concat(local == null ? Array.Empty<string>() : new[] { local.Identifier.ValueText }).ToArray();
		Assert.DoesNotContain(calls, call => call.Expression is IdentifierNameSyntax id && delegates.Contains(id.Identifier.ValueText) ||
			call.Expression is MemberAccessExpressionSyntax invoke && invoke.Name.Identifier.ValueText == "Invoke" &&
			invoke.Expression is IdentifierNameSyntax receiver && delegates.Contains(receiver.Identifier.ValueText));
		var wrapper = syntax.DescendantNodes().OfType<MethodDeclarationSyntax>().Single(m => m.Identifier.ValueText == "ValidateDocumentAsync" && m.ParameterList.Parameters.Count == 3);
		var forwarded = Assert.Single(wrapper.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(call => Name(call) == "ValidateDocumentAsync"));
		Assert.Equal(4, forwarded.ArgumentList.Arguments.Count);
		var budgetCall = Assert.Single(forwarded.ArgumentList.Arguments[3].DescendantNodes().OfType<InvocationExpressionSyntax>());
		Assert.Equal("FromSeconds", Name(budgetCall));
		Assert.Equal("OptionalBudgetSeconds", Assert.Single(budgetCall.ArgumentList.Arguments).ToString());
	}

	private static void Record(string scenario, double elapsed, ValidateFileResult result)
	{
		lock (Timings)
		{
			Timings.Add(new { scenario, phase = scenario == "baseline" ? "baseline-validation" : "validation-after-baseline-warmup", elapsedMilliseconds = elapsed, result.SourceGeneratedDocumentCount });
			var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "issue-70");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "validation-latency.json"), JsonConvert.SerializeObject(Timings, Formatting.Indented));
		}
	}

	private sealed class GeneratorReference : AnalyzerReference
	{
		public override string FullPath => "issue70-in-memory";
		public override object Id => FullPath;
		public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => [];
		public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => [];
		public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => [new Generator().AsSourceGenerator()];
	}

	private sealed class Generator : IIncrementalGenerator
	{
		public void Initialize(IncrementalGeneratorInitializationContext context)
			=> context.RegisterPostInitializationOutput(output => output.AddSource("Generated.g.cs", SourceText.From("static class Generated { public const int Value = 42; }", System.Text.Encoding.UTF8)));
	}

	public void Dispose() => _workspace.Dispose();
}
