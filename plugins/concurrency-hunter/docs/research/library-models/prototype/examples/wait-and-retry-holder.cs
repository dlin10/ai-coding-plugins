// Synthesized driver for M:Polly.RetrySyntax.WaitAndRetry(Polly.PolicyBuilder,System.Int32,System.Func{System.Int32,System.TimeSpan},System.Action{System.Exception,System.TimeSpan,System.Int32,Polly.Context})
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
public static class Probe { public static int P0_Call; public static int P0_Enum; public static int P0_T0; public static int P0_T1; public static int P0_T2; public static int P0_T3; public static int P0_T4; public static int P0_T5; public static int P0_T6; public static int P0_T7; public static int P0_T8; public static int P0_T9; public static int P0_T10; public static int P0_T11; public static int P0_T12; public static int P0_T13; public static int P0_T14; public static int P0_T15; public static int P0_T16; public static int P0_T17; public static int P0_T18; public static int P0_T19; public static int P0_T20; public static int P0_T21; public static int P0_T22; public static int P0_T23; public static int P1_Call; public static int P1_Enum; public static int P1_T0; public static int P1_T1; public static int P1_T2; public static int P1_T3; public static int P1_T4; public static int P1_T5; public static int P1_T6; public static int P1_T7; public static int P1_T8; public static int P1_T9; public static int P1_T10; public static int P1_T11; public static int P1_T12; public static int P1_T13; public static int P1_T14; public static int P1_T15; public static int P1_T16; public static int P1_T17; public static int P1_T18; public static int P1_T19; public static int P1_T20; public static int P1_T21; public static int P1_T22; public static int P1_T23; public static int P2_Call; public static int P2_Enum; public static int P2_T0; public static int P2_T1; public static int P2_T2; public static int P2_T3; public static int P2_T4; public static int P2_T5; public static int P2_T6; public static int P2_T7; public static int P2_T8; public static int P2_T9; public static int P2_T10; public static int P2_T11; public static int P2_T12; public static int P2_T13; public static int P2_T14; public static int P2_T15; public static int P2_T16; public static int P2_T17; public static int P2_T18; public static int P2_T19; public static int P2_T20; public static int P2_T21; public static int P2_T22; public static int P2_T23; public static int P3_Call; public static int P3_Enum; public static int P3_T0; public static int P3_T1; public static int P3_T2; public static int P3_T3; public static int P3_T4; public static int P3_T5; public static int P3_T6; public static int P3_T7; public static int P3_T8; public static int P3_T9; public static int P3_T10; public static int P3_T11; public static int P3_T12; public static int P3_T13; public static int P3_T14; public static int P3_T15; public static int P3_T16; public static int P3_T17; public static int P3_T18; public static int P3_T19; public static int P3_T20; public static int P3_T21; public static int P3_T22; public static int P3_T23; public static int P4_Call; public static int P4_Enum; public static int P4_T0; public static int P4_T1; public static int P4_T2; public static int P4_T3; public static int P4_T4; public static int P4_T5; public static int P4_T6; public static int P4_T7; public static int P4_T8; public static int P4_T9; public static int P4_T10; public static int P4_T11; public static int P4_T12; public static int P4_T13; public static int P4_T14; public static int P4_T15; public static int P4_T16; public static int P4_T17; public static int P4_T18; public static int P4_T19; public static int P4_T20; public static int P4_T21; public static int P4_T22; public static int P4_T23; public static int P5_Call; public static int P5_Enum; public static int P5_T0; public static int P5_T1; public static int P5_T2; public static int P5_T3; public static int P5_T4; public static int P5_T5; public static int P5_T6; public static int P5_T7; public static int P5_T8; public static int P5_T9; public static int P5_T10; public static int P5_T11; public static int P5_T12; public static int P5_T13; public static int P5_T14; public static int P5_T15; public static int P5_T16; public static int P5_T17; public static int P5_T18; public static int P5_T19; public static int P5_T20; public static int P5_T21; public static int P5_T22; public static int P5_T23; public static int P6_Call; public static int P6_Enum; public static int P6_T0; public static int P6_T1; public static int P6_T2; public static int P6_T3; public static int P6_T4; public static int P6_T5; public static int P6_T6; public static int P6_T7; public static int P6_T8; public static int P6_T9; public static int P6_T10; public static int P6_T11; public static int P6_T12; public static int P6_T13; public static int P6_T14; public static int P6_T15; public static int P6_T16; public static int P6_T17; public static int P6_T18; public static int P6_T19; public static int P6_T20; public static int P6_T21; public static int P6_T22; public static int P6_T23; public static int P7_Call; public static int P7_Enum; public static int P7_T0; public static int P7_T1; public static int P7_T2; public static int P7_T3; public static int P7_T4; public static int P7_T5; public static int P7_T6; public static int P7_T7; public static int P7_T8; public static int P7_T9; public static int P7_T10; public static int P7_T11; public static int P7_T12; public static int P7_T13; public static int P7_T14; public static int P7_T15; public static int P7_T16; public static int P7_T17; public static int P7_T18; public static int P7_T19; public static int P7_T20; public static int P7_T21; public static int P7_T22; public static int P7_T23; }
public static class Keep { public static object? R; public static object? Recv; }
public sealed class Marker { }
public sealed class Gen0 { }
public sealed class Gen1 { }
public sealed class Gen2 { }
public sealed class Gen3 { }

public sealed class DriverController : ControllerBase
{
    public async global::System.Threading.Tasks.Task<IActionResult> V_Call()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_Call_0(), L_P3_Call_1());
        Keep.R = r; 
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T0()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T0_2(), L_P3_T0_3());
        _ = r.WithPolicyKey("s");
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T1()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T1_4(), L_P3_T1_5());
        r.Execute(((global::System.Action)(() => { })));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T2()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T2_6(), L_P3_T2_7());
        r.Execute(((global::System.Action<global::Polly.Context>)((global::Polly.Context a0) => { })), new global::System.Collections.Generic.Dictionary<string, object>());
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T3()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T3_8(), L_P3_T3_9());
        r.Execute(((global::System.Action<global::Polly.Context>)((global::Polly.Context a0) => { })), new global::Polly.Context());
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T4()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T4_10(), L_P3_T4_11());
        r.Execute(((global::System.Action<global::System.Threading.CancellationToken>)((global::System.Threading.CancellationToken a0) => { })), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T5()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T5_12(), L_P3_T5_13());
        r.Execute(((global::System.Action<global::Polly.Context, global::System.Threading.CancellationToken>)((global::Polly.Context a0, global::System.Threading.CancellationToken a1) => { })), new global::System.Collections.Generic.Dictionary<string, object>(), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T6()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T6_14(), L_P3_T6_15());
        r.Execute(((global::System.Action<global::Polly.Context, global::System.Threading.CancellationToken>)((global::Polly.Context a0, global::System.Threading.CancellationToken a1) => { })), new global::Polly.Context(), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T7()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T7_16(), L_P3_T7_17());
        _ = r.ExecuteAndCapture(((global::System.Action)(() => { })));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T8()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T8_18(), L_P3_T8_19());
        _ = r.ExecuteAndCapture(((global::System.Action<global::Polly.Context>)((global::Polly.Context a0) => { })), new global::System.Collections.Generic.Dictionary<string, object>());
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T9()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T9_20(), L_P3_T9_21());
        _ = r.ExecuteAndCapture(((global::System.Action<global::Polly.Context>)((global::Polly.Context a0) => { })), new global::Polly.Context());
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T10()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T10_22(), L_P3_T10_23());
        _ = r.ExecuteAndCapture(((global::System.Action<global::System.Threading.CancellationToken>)((global::System.Threading.CancellationToken a0) => { })), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T11()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T11_24(), L_P3_T11_25());
        _ = r.ExecuteAndCapture(((global::System.Action<global::Polly.Context, global::System.Threading.CancellationToken>)((global::Polly.Context a0, global::System.Threading.CancellationToken a1) => { })), new global::System.Collections.Generic.Dictionary<string, object>(), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T12()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T12_26(), L_P3_T12_27());
        _ = r.ExecuteAndCapture(((global::System.Action<global::Polly.Context, global::System.Threading.CancellationToken>)((global::Polly.Context a0, global::System.Threading.CancellationToken a1) => { })), new global::Polly.Context(), default(global::System.Threading.CancellationToken));
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    public async global::System.Threading.Tasks.Task<IActionResult> T13()
    {
        var r = global::Polly.RetrySyntax.WaitAndRetry(global::Polly.Policy.Handle<global::System.Exception>(), default(int), L_P2_T13_28(), L_P3_T13_29());
        _ = r.Wrap(global::Polly.Policy.NoOp());
        await global::System.Threading.Tasks.Task.Yield();
        return Ok();
    }
    private static global::System.Func<int, global::System.TimeSpan> L_P2_Call_0() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_Call = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_Call_1() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_Call = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T0_2() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T0 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T0_3() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T0 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T1_4() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T1 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T1_5() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T1 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T2_6() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T2 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T2_7() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T2 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T3_8() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T3 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T3_9() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T3 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T4_10() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T4 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T4_11() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T4 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T5_12() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T5 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T5_13() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T5 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T6_14() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T6 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T6_15() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T6 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T7_16() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T7 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T7_17() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T7 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T8_18() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T8 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T8_19() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T8 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T9_20() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T9 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T9_21() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T9 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T10_22() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T10 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T10_23() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T10 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T11_24() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T11 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T11_25() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T11 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T12_26() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T12 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T12_27() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T12 = 1; }));
    private static global::System.Func<int, global::System.TimeSpan> L_P2_T13_28() => ((global::System.Func<int, global::System.TimeSpan>)((int a0) => { Probe.P2_T13 = 1; return default!; }));
    private static global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context> L_P3_T13_29() => ((global::System.Action<global::System.Exception, global::System.TimeSpan, int, global::Polly.Context>)((global::System.Exception a0, global::System.TimeSpan a1, int a2, global::Polly.Context a3) => { Probe.P3_T13 = 1; }));

}

public static class Startup
{
    public static void Configure(IServiceCollection services, IEndpointRouteBuilder app)
    {
        services.AddControllers();
        app.MapControllers();
        services.AddSingleton<Marker>(_ => new Marker());
    }
}