using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Core.Tests.Generation;

/// <summary>A member a call may dispatch away from has no answer (SPEC TD-034b, G-6): a run applies a model by the member a call names,
/// and the body the driver would analyse is not the one an override of another type runs.</summary>
public sealed class OverridableMemberTests
{
    [Theory]
    [InlineData("public class Service { public virtual void Run(Action action) { action(); } }", "M:Lib.Service.Run(System.Action)")]
    [InlineData("public class Base { public virtual void Run(Action action) { } } public class Service : Base { public override void Run(Action action) { action(); } }",
                "M:Lib.Service.Run(System.Action)")]
    [InlineData("public abstract class Service { public virtual int Count(object value) => 0; }", "M:Lib.Service.Count(System.Object)")]
    [InlineData("public class Service { public virtual object Value { get; set; } }", "M:Lib.Service.set_Value(System.Object)")]
    public void A_member_a_call_may_dispatch_away_from_is_overridable(string types, string id)
    {
        // MethodInfo.CreateDelegate(Type, object): its own body throws, and RuntimeMethodInfo's override, which runs for every method a
        // program gets, keeps the target.
        var answer = GenerationRuns.Trace(types, id).Answer;

        Assert.Equal(GenerationReasons.OVERRIDABLE, answer.Reason);
        Assert.Null(answer.Classified);
        Assert.Null(answer.Model);
        Assert.Contains(id, answer.Detail);
    }

    [Theory]
    [InlineData("public class Service { public void Run(Action action) { action(); } }", "M:Lib.Service.Run(System.Action)")]
    [InlineData("public class Base { public virtual void Run(Action action) { } } public class Service : Base { public sealed override void Run(Action action) { action(); } }",
                "M:Lib.Service.Run(System.Action)")]
    [InlineData("public class Base { public virtual void Run(Action action) { } } public sealed class Service : Base { public override void Run(Action action) { action(); } }",
                "M:Lib.Service.Run(System.Action)")]
    public void A_member_no_call_dispatches_away_from_is_answered(string types, string id)
    {
        var answer = GenerationRuns.Trace(types, id).Answer;

        Assert.Null(answer.Reason);
        Assert.NotNull(answer.Model);
    }
}
