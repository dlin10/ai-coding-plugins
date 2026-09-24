using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using RoslynMcpExtension.Services;
using RoslynMcpExtension.Shared;
using Xunit;
using static RoslynMcpExtension.Tests.TestWorkspace;

namespace RoslynMcpExtension.Tests;

public sealed class SymbolIdTests : IDisposable
{
	private const string GetInt = "M:Ns.Api.Get(System.Int32)";
	private const string WidgetCtor = "M:Ns.Widget.#ctor(System.Int32)";

	private const string Lib = """
		namespace Ns
		{
		    public static class Api
		    {
		        public static int Get(int x) => x;
		        public static int Get(string x) => 0;
		        public static int Multi()
		        {
		            return 1;
		        }
		    }
		    public class Widget { public Widget(int size) { } }
		}
		""";

	private const string App = """
		namespace Ns
		{
		    class Use
		    {
		        int U() => Api.Get(1);
		        int V() => Api.Get("a");
		        System.Func<int> W() => () => Api.Get(2);
		        int L() { var local = 1; return local; }
		        Widget N() => new Widget(3);
		    }
		}
		""";

	private readonly TestWorkspace _solution = new();

	public SymbolIdTests()
	{
		// App comes first, so an ID declared in Lib is met through App's reference before Lib itself.
		var lib = ProjectId.CreateNewId("Lib");
		_solution.AddProject(ProjectId.CreateNewId("App"), "App", App, [lib], []);
		_solution.AddProject(lib, "Lib", Lib, [], []);
	}

	[Fact]
	public async Task ReferencesBySymbolIdFindTheOverloadItNamesWithTheirMembers()
	{
		var result = await new FindReferencesService(_solution.Finder).FindReferencesAsync(null, 0, 0, GetInt, null, 50);

		Assert.Null(result.ErrorMessage);
		Assert.Equal("Lib", result.Compilation!.ProjectName);
		var containing = result.Members.Where(m => m.MemberType == "reference").Select(m => m.ContainingSymbolId).ToList();
		Assert.Equal(["M:Ns.Use.U", "M:Ns.Use.W"], containing.OrderBy(id => id, StringComparer.Ordinal));
	}

	[Fact]
	public async Task SearchResultsCarryIdsAndWholeDeclarationSpans()
	{
		var search = await new SearchSymbolsService(_solution.Finder).SearchSymbolsAsync("Multi", false, 10);

		var multi = Assert.Single(search.Members);
		Assert.Equal("M:Ns.Api.Multi", multi.SymbolId);
		Assert.Equal((7, 7, 10), (multi.StartLine, multi.EnclosingStartLine, multi.EnclosingEndLine));

		var info = await new SymbolInfoService(_solution.Finder).GetSymbolInfoAsync(null, 0, 0, multi.SymbolId, null);
		Assert.Equal(multi.FullName, info.Symbol!.FullName);
	}

	[Fact]
	public async Task CallersNameTheCallingMemberForTheNextStepUp()
	{
		var result = await new FindCallersService(_solution.Finder).FindCallersAsync(null, 0, 0, GetInt, null, 50);

		Assert.Null(result.ErrorMessage);
		Assert.Contains(result.Members, m => m.ContainingSymbolId == "M:Ns.Use.U");
		Assert.Contains(result.Members, m => m.ContainingSymbolId == "M:Ns.Use.W");
		Assert.DoesNotContain(result.Members, m => m.ContainingSymbolId == "M:Ns.Use.V");
	}

	[Fact]
	public async Task AnIdWithoutADeclarationIndexNameStillResolvesInTheDeclaringProject()
	{
		var result = await new FindReferencesService(_solution.Finder).FindReferencesAsync(null, 0, 0, WidgetCtor, null, 50);

		Assert.Null(result.ErrorMessage);
		Assert.Equal("Lib", result.Compilation!.ProjectName);
		Assert.Contains(result.Members, m => m.ContainingSymbolId == "M:Ns.Use.N");
	}

	[Fact]
	public async Task AMetadataIdResolvesInTheFirstProjectAndNamesItsAssembly()
	{
		var info = await new SymbolInfoService(_solution.Finder).GetSymbolInfoAsync(null, 0, 0, "T:System.String", null);

		Assert.Null(info.ErrorMessage);
		Assert.Equal("App", info.Compilation!.ProjectName);
		Assert.Null(info.Compilation.DocumentErrorCount);
		Assert.StartsWith("mscorlib ", info.Symbol!.Assembly);
	}

	[Fact]
	public async Task ProjectNameChoosesTheContext()
	{
		var info = await new SymbolInfoService(_solution.Finder).GetSymbolInfoAsync(null, 0, 0, GetInt, "App");

		Assert.Equal("App", info.Compilation!.ProjectName);
		Assert.Equal(GetInt, info.Symbol!.SymbolId);
	}

	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public async Task APositionAndAnIdAreEitherOr(bool position, bool id)
	{
		var info = await new SymbolInfoService(_solution.Finder)
			.GetSymbolInfoAsync(position ? PathOf("App") : null, 5, 21, id ? GetInt : null, null);

		Assert.Equal(ToolErrorCodes.InvalidArgument, info.ErrorCode);
	}

	[Fact]
	public async Task AnUnknownIdIsAnInvalidArgument()
	{
		var info = await new SymbolInfoService(_solution.Finder).GetSymbolInfoAsync(null, 0, 0, "M:Ns.Api.Missing", null);

		Assert.Equal(ToolErrorCodes.InvalidArgument, info.ErrorCode);
	}

	[Fact]
	public async Task ALocalHasNoId()
	{
		var info = await new SymbolInfoService(_solution.Finder).GetSymbolInfoAsync(PathOf("App"), 8, 23, null, null);

		Assert.Equal("local", info.Symbol!.Name);
		Assert.Null(info.Symbol.SymbolId);
	}

	[Fact]
	public async Task IncludeMetadataFindsReferencedTypesByExactName()
	{
		var service = new SearchSymbolsService(_solution.Finder);

		var withMetadata = await service.SearchSymbolsAsync("String", true, 50);
		var type = Assert.Single(withMetadata.Members, m => m.SymbolId == "T:System.String");
		Assert.Empty(type.FilePath);
		Assert.StartsWith("mscorlib ", type.Assembly);

		Assert.DoesNotContain((await service.SearchSymbolsAsync("String", false, 50)).Members, m => m.SymbolId == "T:System.String");
	}

	public void Dispose() => _solution.Dispose();
}
