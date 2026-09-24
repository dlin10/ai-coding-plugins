using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;
using static RoslynMcpExtension.Tests.TestWorkspace;

namespace RoslynMcpExtension.Tests;

public sealed class DescribeTypeServiceTests : IDisposable
{
	private const string Widget = "T:Ns.Widget";

	private const string Lib = """
		namespace Ns
		{
		    public class Base { public void Inherited() { } }
		    public class Widget : Base, System.IDisposable
		    {
		        public Widget(int size) { }
		        /// <summary>Resizes the <see cref="T:Ns.Widget"/> to <paramref name="size"/>.</summary>
		        public void Resize(int size) { }
		        internal int Hidden => 0;
		        protected void ForDerived() { }
		        private void Secret() { }
		        [System.Obsolete] public void Old() { }
		        public void Dispose() { }
		    }
		    public static class WidgetExtensions
		    {
		        public static int Area(this Widget widget) => 0;
		        public static void Touch<T>(this T anything) where T : class { }
		        public static void Poke(this object anything) { }
		    }
		}
		namespace Aaa
		{
		    public static class Early { public static int Aardvark(this Ns.Widget widget) => 0; }
		}
		namespace Zed
		{
		    public static class ZedExtensions { public static int Zap<T>(this System.Collections.Generic.List<T> list) => 0; }
		}
		""";

	// Imports Zed, not System.Linq, so at the list's position only Zed's extension is in reach.
	private const string App = """
		using Zed;
		namespace Ns
		{
		    class Use
		    {
		        void M()
		        {
		            var list = new System.Collections.Generic.List<int>();
		        }
		    }
		}
		""";

	private readonly TestWorkspace _solution = new();

	public DescribeTypeServiceTests()
	{
		var lib = _solution.AddProject(ProjectId.CreateNewId("Lib"), "Lib", Lib, [], [SystemCore]);
		_solution.AddProject(ProjectId.CreateNewId("App"), "App", App, [lib], [SystemCore]);
	}

	private Task<TypeDescriptionResult> DescribeAsync(string? filePath = null, int line = 0, int column = 0, string? symbolId = null,
	                                                  string? projectName = null, string? memberFilter = null, int maxResults = 100,
	                                                  TimeSpan? budget = null)
		=> new DescribeTypeService(_solution.Finder, budget ?? TimeSpan.FromMinutes(1))
			.DescribeTypeAsync(filePath, line, column, symbolId, projectName, memberFilter, maxResults);

	[Fact]
	public async Task ASourceTypeShowsItsChainItsOwnUsableMembersAndItsExtensions()
	{
		var result = await DescribeAsync(symbolId: Widget);

		Assert.Null(result.ErrorMessage);
		Assert.Equal("Lib", result.Compilation!.ProjectName);
		Assert.Equal(["Base", "Object"], result.BaseTypes.Select(t => t.Name));
		Assert.Contains(result.Interfaces, i => i.SymbolId == "T:System.IDisposable");

		var names = result.Members.Select(m => m.Name).ToList();
		Assert.Equal([".ctor", "Hidden", "Dispose", "ForDerived", "Old", "Resize"], names);
		Assert.Equal("Resizes the Ns.Widget to size.", result.Members.Single(m => m.Name == "Resize").Summary);
		Assert.True(result.Members.Single(m => m.Name == "Old").Obsolete);

		// Area shares the type's namespace, so it leads Aardvark although Aaa sorts first.
		Assert.Equal(["Area", "Aardvark"], result.Extensions.Select(e => e.Name));
		var extension = result.Extensions[0];
		Assert.Equal(("Area", "Ns", "M:Ns.WidgetExtensions.Area(Ns.Widget)"), (extension.Name, extension.Namespace, extension.SymbolId));
		Assert.True(result.ExtensionsComplete);
	}

	[Fact]
	public async Task AnotherProjectSeesNoInternalMembersButKeepsProtectedOnes()
	{
		var result = await DescribeAsync(symbolId: Widget, projectName: "App");

		Assert.Equal("App", result.Compilation!.ProjectName);
		Assert.DoesNotContain(result.Members, m => m.Name == "Hidden");
		Assert.Contains(result.Members, m => m.Name == "ForDerived");
	}

	[Fact]
	public async Task APositionDescribesTheVariablesTypeWithTheExtensionsInReach()
	{
		// LINQ alone declares more extensions for IEnumerable<T> than the default limit leaves room for.
		var result = await DescribeAsync(PathOf("App"), 8, 17, maxResults: 500);

		Assert.Null(result.ErrorMessage);
		Assert.False(result.Truncated);
		Assert.Equal("T:System.Collections.Generic.List`1", result.Type!.SymbolId);
		Assert.StartsWith("mscorlib ", result.Type.Assembly);
		Assert.Contains("int item", result.Members.First(m => m.Name == "Add").Signature);
		var where = result.Extensions.First(m => m.Name == "Where");
		Assert.Equal("System.Linq", where.Namespace);
		Assert.StartsWith("System.Core ", where.Assembly);
	}

	[Fact]
	public async Task ExtensionsInReachAtThePositionComeFirstAndOnlyThere()
	{
		static int IndexOf(TypeDescriptionResult result, string name) => result.Extensions.FindIndex(e => e.Name == name);

		// At the position, App imports Zed and not System.Linq.
		var atPosition = await DescribeAsync(PathOf("App"), 8, 17, maxResults: 500);
		Assert.Equal("Zap", atPosition.Extensions[0].Name);

		// Named by ID there is no position; System.Linq shares "System" with System.Collections.Generic, Zed nothing.
		var byId = await DescribeAsync(symbolId: "T:System.Collections.Generic.List`1", projectName: "App", maxResults: 500);
		Assert.True(IndexOf(byId, "Where") < IndexOf(byId, "Zap"));
	}

	[Theory]
	[InlineData("TryGetAsync", "TGA", true)]
	[InlineData("TryGetAsync", "GA", true)]
	[InlineData("AddSingleton", "AddSin", true)]
	[InlineData("TryAddSingleton", "AddSingleton", true)]
	[InlineData("AddDiSingletonControllerVsWorker", "AddSingleton", false)]
	[InlineData("TryGetAsync", "TrAs", false)]
	public void MemberFilterHumpsMustFollowOneAnother(string name, string filter, bool matches)
		=> Assert.Equal(matches, DescribeTypeService.Matches(name, filter));

	[Fact]
	public async Task MemberFilterNarrowsMembersAndExtensionsAlike()
	{
		var byName = await DescribeAsync(symbolId: Widget, memberFilter: "res");
		Assert.Equal("Resize", Assert.Single(byName.Members).Name);
		Assert.Empty(byName.Extensions);

		var byHumps = await DescribeAsync(symbolId: Widget, memberFilter: "FD");
		Assert.Equal("ForDerived", Assert.Single(byHumps.Members).Name);
	}

	[Fact]
	public async Task AnExhaustedBudgetStillReturnsTheDeclaredMembers()
	{
		var result = await DescribeAsync(symbolId: Widget, budget: TimeSpan.Zero);

		Assert.Null(result.ErrorMessage);
		Assert.NotEmpty(result.Members);
		Assert.False(result.ExtensionsComplete);
		Assert.Contains("Lib", result.UnscannedAssemblies);
		Assert.Contains("mscorlib", result.UnscannedAssemblies);
	}

	[Fact]
	public async Task TheLimitCutsExtensionsBeforeDeclaredMembers()
	{
		var result = await DescribeAsync(PathOf("App"), 8, 17, maxResults: 2);

		Assert.Equal(2, result.Members.Count);
		Assert.Empty(result.Extensions);
		Assert.True(result.Truncated);
		Assert.True(result.ExtensionCount > 0);
	}

	[Fact]
	public async Task ANamespaceHasNoTypeToDescribe()
	{
		Assert.Equal(ToolErrorCodes.InvalidArgument, (await DescribeAsync(symbolId: "N:Ns")).ErrorCode);
	}

	public void Dispose() => _solution.Dispose();
}
