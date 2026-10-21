using System.Collections.Concurrent;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Microsoft.CodeAnalysis;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Providers;

public sealed class CandidacyMatrixTests
{
    private enum Member { StaticMethod, InstanceMethod, Constructor, StaticGetter, InstanceGetter }
    private enum Receiver { None, Candidate, NotCandidate }
    private enum Parameter { None, Candidate, NotCandidate, OutCandidate }
    private enum Result { Void, NotCandidate, Candidate, TaskOfCandidate, TaskOfNotCandidate, ValueTaskOfCandidate, ValueTaskOfNotCandidate }
    private sealed record Cell(Member Member, Receiver Receiver, Parameter Parameter, Result Result);

    private static readonly Cell[] Cells = (from member in Enum.GetValues<Member>()
                                          from receiver in Enum.GetValues<Receiver>()
                                          from parameter in Enum.GetValues<Parameter>()
                                          from result in Enum.GetValues<Result>()
                                          where Allowed(member, receiver, parameter, result)
                                          select new Cell(member, receiver, parameter, result)).ToArray();

    [Fact]
    public void Every_cell_gets_its_expected_candidacy()
    {
        var wrong = new ConcurrentQueue<string>();
        Parallel.ForEach(Cells, CellParallelism, cell =>
        {
            try { Check(cell); }
            catch (Exception error) { wrong.Enqueue($"{cell}: {error.Message}"); }
        });
        Assert.True(wrong.IsEmpty, $"{wrong.Count} of {Cells.Length} cells:\n" + string.Join('\n', wrong));
    }

    [Fact]
    public void Every_axis_value_occurs_in_a_cell()
    {
        Assert.Equal(Cells.Length, Cells.Distinct().Count());
        Assert.Equal(Enum.GetValues<Member>(), Cells.Select(cell => cell.Member).Distinct().Order());
        Assert.Equal(Enum.GetValues<Receiver>(), Cells.Select(cell => cell.Receiver).Distinct().Order());
        Assert.Equal(Enum.GetValues<Parameter>(), Cells.Select(cell => cell.Parameter).Distinct().Order());
        Assert.Equal(Enum.GetValues<Result>(), Cells.Select(cell => cell.Result).Distinct().Order());
    }

    private static bool Allowed(Member member, Receiver receiver, Parameter parameter, Result result)
    {
        // Static members and constructors have no receiver input; instance members require one.
        if (member is Member.StaticMethod or Member.StaticGetter or Member.Constructor)
        {
            if (receiver != Receiver.None)
                return false;
        }
        else if (receiver == Receiver.None)
            return false;
        // A property getter has no parameters and cannot return void.
        if (member is Member.StaticGetter or Member.InstanceGetter && (parameter != Parameter.None || result == Result.Void))
            return false;
        // A constructor's result is its created class or plain struct, never a declared return type.
        if (member == Member.Constructor && result is not (Result.Candidate or Result.NotCandidate))
            return false;
        return true;
    }

    private static void Check(Cell cell)
    {
        var library = Compile(LibrarySource(cell));
        Assert.True(library.Compilation is not null, $"{library.Reason}: {string.Join(", ", library.Errors)}");
        Assert.Empty(library.ExternMembers);
        var type = library.Compilation.GetTypeByMetadataName("Lib.Target")!;
        var member = cell.Member switch
        {
            Member.Constructor => type.InstanceConstructors.Single(constructor => !constructor.IsImplicitlyDeclared),
            Member.StaticGetter or Member.InstanceGetter => type.GetMembers("Run").OfType<IPropertySymbol>().Single().GetMethod!,
            _ => type.GetMembers("Run").OfType<IMethodSymbol>().Single()
        };
        var synthesis = DriverSynthesizer.Synthesize(library.Compilation, member, library.ExternMembers, library.OpenedFields, CancellationToken.None);
        var expected = cell.Receiver == Receiver.Candidate || cell.Parameter == Parameter.Candidate ||
                       cell.Result is Result.Candidate or Result.TaskOfCandidate or Result.ValueTaskOfCandidate;
        if (expected)
            Assert.NotEqual(GenerationReasons.NOT_A_CANDIDATE, synthesis.Reason);
        else
        {
            Assert.Equal(GenerationReasons.NOT_A_CANDIDATE, synthesis.Reason);
            Assert.Equal($"{member.GetDocumentationCommentId()} has no receiver, parameter or result that can hold a user object", synthesis.Detail);
        }
    }

    private static string LibrarySource(Cell cell)
    {
        var result = cell.Result switch
        {
            Result.Void => "void",
            Result.NotCandidate => "int",
            Result.Candidate => "Value",
            Result.TaskOfCandidate => "Task<Value>",
            Result.TaskOfNotCandidate => "Task<int>",
            Result.ValueTaskOfCandidate => "ValueTask<Value>",
            Result.ValueTaskOfNotCandidate => "ValueTask<int>",
            _ => throw new ArgumentOutOfRangeException()
        };
        var parameter = cell.Parameter switch
        {
            Parameter.None => "",
            Parameter.Candidate => "Value value",
            Parameter.NotCandidate => "int value",
            Parameter.OutCandidate => "out Value value",
            _ => throw new ArgumentOutOfRangeException()
        };
        var candidateType = cell.Member == Member.Constructor ? cell.Result == Result.Candidate : cell.Receiver != Receiver.NotCandidate;
        var declaration = candidateType ? "sealed class" : "struct";
        var field = candidateType ? "public object Slot;" : "public int Number;";
        var body = cell.Parameter == Parameter.OutCandidate ? "value = null; " : "";
        string member;
        if (cell.Member == Member.Constructor)
            member = $"public Target({parameter}) {{ {body}{(candidateType ? "Slot = null;" : "Number = 0;")} }}";
        else
        {
            var modifier = cell.Member is Member.StaticMethod or Member.StaticGetter ? "static " : "";
            body += cell.Result == Result.Void ? "" : "return default;";
            member = cell.Member is Member.StaticGetter or Member.InstanceGetter
                ? $"public {modifier}{result} Run {{ get {{ {body} }} }}"
                : $"public {modifier}{result} Run({parameter}) {{ {body} }}";
        }
        return $$"""
            using System.Threading.Tasks;
            namespace Lib
            {
                public sealed class Value { public object Slot; }
                public {{declaration}} Target
                {
                    {{field}}
                    {{member}}
                }
            }
            """;
    }
}
