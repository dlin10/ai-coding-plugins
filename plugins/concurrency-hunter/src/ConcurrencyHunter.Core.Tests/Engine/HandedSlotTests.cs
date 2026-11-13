using ConcurrencyHunter.Accesses;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Heap;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.EngineFixture;

namespace ConcurrencyHunter.Core.Tests.Engine;

public sealed class HandedSlotTests
{
    [Theory]
    [InlineData("ParameterDirect")]
    [InlineData("ReceiverDirect")]
    [InlineData("Assignment")]
    [InlineData("Conversion")]
    [InlineData("PhiOfOneParameter")]
    [InlineData("PhiWithAnotherParameter")]
    [InlineData("PhiWithReceiver")]
    [InlineData("PhiWithAllocation")]
    [InlineData("PhiWithNull")]
    [InlineData("PhiWithDefault")]
    public void Handed_slot_follows_the_definitions_of_one_parameter_or_the_receiver(string form)
    {
        var body = form switch
        {
            "ParameterDirect" => "h.Hits = 1;",
            "ReceiverDirect" => "Hits = 1;",
            "Assignment" => "var x = h; x.Hits = 1;",
            "Conversion" => "var x = (Hook)(object)h; x.Hits = 1;",
            "PhiOfOneParameter" => "var x = h; if (c) x = h; x.Hits = 1;",
            "PhiWithAnotherParameter" => "var x = h; if (c) x = other; x.Hits = 1;",
            "PhiWithReceiver" => "var x = h; if (c) x = this; x.Hits = 1;",
            "PhiWithAllocation" => "var x = h; if (c) x = new Hook(); x.Hits = 1;",
            "PhiWithNull" => "var x = h; if (c) x = null; x!.Hits = 1;",
            "PhiWithDefault" => "var x = h; if (c) x = default; x!.Hits = 1;",
            _ => throw new ArgumentOutOfRangeException(nameof(form))
        };
        var expected = form == "ReceiverDirect" ? new HandedSlot(null)
            : form is "ParameterDirect" or "Assignment" or "Conversion" or "PhiOfOneParameter" ? new HandedSlot(0) : null;
        var run = Analyze($$"""
            using System.Collections.Generic;
            public sealed class Hook
            {
                public int Hits;
                public void Write(Hook h, Hook other, bool c) { {{body}} }
            }
            public sealed class Repository { public List<Hook> Items = new(); public bool Flag; }
            public class HooksController(Repository repository) : ControllerBase
            {
                public void Post()
                {
                    var h = new Hook();
                    repository.Items.Add(h);
                    h.Write(h, new Hook(), repository.Flag);
                }
            }
            """ + Startup("services.AddSingleton<Repository>();"));

        var instances = run.Execution.Heap.Heap.Instances.Values.Where(instance =>
            instance.Summary.Accesses.Any(access => access.Field.Name == "Hits" && access.Kind == SummaryAccessKind.Store)).ToArray();
        var helper = Assert.Single(instances);
        var write = Assert.Single(helper.Summary.Accesses, access => access.Field.Name == "Hits" && access.Kind == SummaryAccessKind.Store);
        Assert.Equal(expected, write.HandedSlot);
        Assert.False(write.IsFresh);
        var writes = run.Of("Hits").Where(access => access.Operation == AccessOperation.Write).ToArray();
        Assert.NotEmpty(writes);
        Assert.All(writes, access => Assert.Equal(expected is not null, access.IsFresh));
        var program = run.Execution.Heap.Program;
        var scope = new ScopeProgram(program.ScopeId, program.Input.Roots, program.Result, run.Execution.Heap.Summaries,
                                     program.Input.Program, program.Input.DiIndex, program.Input.InjectionBindings);
        var forward = new HandedFreshness(scope, run.Execution.Heap.Heap, run.Execution.Analysis);
        var reverse = new HandedFreshness(scope, run.Execution.Heap.Heap, run.Execution.Analysis, reverseOrder: true);
        Assert.Equal(forward.IsFresh(helper.Id, expected), reverse.IsFresh(helper.Id, expected));
    }
}
