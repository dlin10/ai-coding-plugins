using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed class InitializerGenericHelperTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void A_closed_initializer_reached_through_a_generic_helper_is_left_out_and_scanned(bool work, bool automatic)
    {
        var source = PRELUDE + $$"""
            public static class Ext { public static extern void Take(ref int value); }
            public static class State<T>
            {
                public static int Value;
                static State() { {{(work ? "_ = Task.Run(() => Value++);" : "Ext.Take(ref Value);")}} }
            }
            public static class Api
            {
                {{(automatic ? "static Api() { Huge.All(); }" : "")}}
                private static int Read<U>() => State<U>.Value;
                public static int Run(Action a) { a(); return Read<int>(); }
            }
            """ + (automatic ? Huge() : "") + "}";
        var library = Compile(source);
        var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
        var off = ModelGenerator.Trace(request, library, CancellationToken.None,
                                       initializers: automatic ? null : TypeInitializerTreatment.Off);
        Assert.True(off.Run is not null, $"{off.Answer.Reason}: {off.Answer.Detail}");
        Assert.Equal("off", off.Answer.Generation.Mode);
        if (automatic)
            Assert.True(off.Answer.Generation.Attempts[0].ReachedBodies > 1500);
        Assert.DoesNotContain(off.Run!.Reachable.ReachedBodies.Keys, body => body.Contains("State`1.#cctor", StringComparison.Ordinal));
        Assert.Null(off.Answer.Model);
        Assert.Equal(work ? ModelReasons.INCOMPLETE : ModelReasons.LIBRARY_STATE, off.Answer.ModelReason);
        Assert.Contains(off.Answer.Causes, cause => cause.Code == (work ? ModelCauses.INITIALIZER_WORK_NOT_RUN : ModelCauses.LIBRARY_STATE_STORE));
        if (!automatic)
        {
            foreach (var treatment in new[] { TypeInitializerTreatment.Run, TypeInitializerTreatment.Inert })
            {
                var twin = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: treatment);
                if (treatment == TypeInitializerTreatment.Inert)
                    Assert.Contains(twin.Run!.Reachable.TypeInitializers, candidate => candidate.ClosedTypeKeys.Contains(ASSEMBLY + ":Lib.State<System.Private.CoreLib:int>"));
                if (!work)
                    Assert.Contains(twin.Answer.Causes, cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
                else
                    Assert.Contains(twin.Run!.Executions!.Executions, execution => execution.ParentId is { } parent &&
                        twin.Run.Executions.Executions.Any(owner => owner.Id == parent && owner.Kind == ConcurrencyHunter.Execution.ExecutionKind.TypeInitializer));
            }
        }
    }

    private static string Huge() => "public static class Huge { public static void All() { " +
        string.Join(" ", Enumerable.Range(0, 1501).Select(i => $"M{i}();")) + " } " +
        string.Join(" ", Enumerable.Range(0, 1501).Select(i => $"private static void M{i}() {{ }}")) + " }";
}
