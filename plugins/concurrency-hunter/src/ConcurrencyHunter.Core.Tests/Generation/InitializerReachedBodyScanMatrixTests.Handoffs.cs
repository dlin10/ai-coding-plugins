using System.Collections.Concurrent;
using ConcurrencyHunter.CallGraph;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Fixtures.GenerationRuns;

namespace ConcurrencyHunter.Core.Tests.Generation;

public sealed partial class InitializerReachedBodyScanMatrixTests
{
    private enum Receiver { StaticNoBody, StaticBody, NonVirtualBody, VirtualUnknown, VirtualKnown, InterfaceUnknown, InterfaceKnown, Constrained }
    private enum Channel { Ref, In, Out, StructReceiver }
    private sealed record HandoffCell(Receiver Receiver, Channel Channel, int Helpers, bool Lambda);
    private sealed record HandoffResult(GeneratedAnswer Run, GeneratedAnswer Off);
    private static readonly Lazy<IReadOnlyDictionary<HandoffCell, HandoffResult>> Handoffs = new(HandoffResults);

    private static IEnumerable<HandoffCell> HandoffCells() =>
        (from receiver in Enum.GetValues<Receiver>() from channel in Enum.GetValues<Channel>()
         from helpers in new[] { 0, 1, 2 } from lambda in new[] { false, true }
         select new HandoffCell(receiver, channel, helpers, lambda)).Where(cell => Allowed(cell) is null);

    private static string? Allowed(HandoffCell cell) =>
        cell.Channel == Channel.StructReceiver && cell.Receiver is Receiver.VirtualUnknown or Receiver.VirtualKnown or
            Receiver.InterfaceUnknown or Receiver.InterfaceKnown
            ? "A class/interface receiver boxes the struct and does not pass its storage by reference."
            : null;

    [Fact]
    public void Off_refuses_wherever_running_initializers_hand_a_static_to_unseen_code()
    {
        foreach (var (cell, result) in Handoffs.Value)
        {
            var running = result.Run.Causes.Any(cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
            var off = result.Off.Causes.Any(cause => cause.Code == ModelCauses.LIBRARY_STATE_STORE);
            Assert.True(!running || off, $"Off lost the Run handoff: {cell}");
            // Known-wider: Run resolves these two receivers; Off has no heap to prove them (D-0021).
            // The constrained struct receiver in this fixture remains unresolved in Run too.
            var knownWider = cell.Receiver is Receiver.VirtualKnown or Receiver.InterfaceKnown;
            Assert.True((off && !running) == knownWider, $"Unexpected Run/Off difference: {cell}, run={running}, off={off}, run reason={result.Run.ModelReason}");
            if (running || knownWider)
            {
                Assert.Equal(ModelReasons.LIBRARY_STATE, result.Off.ModelReason);
                Assert.Null(result.Off.Model);
            }
            else
                Assert.NotNull(result.Off.Model);
        }
    }

    [Fact]
    public void Every_handoff_axis_value_occurs_in_a_cell()
    {
        var cells = HandoffCells().ToArray();
        Assert.Equal(Enum.GetValues<Receiver>(), cells.Select(cell => cell.Receiver).Distinct().Order());
        Assert.Equal(Enum.GetValues<Channel>(), cells.Select(cell => cell.Channel).Distinct().Order());
        Assert.Equal(new[] { 0, 1, 2 }, cells.Select(cell => cell.Helpers).Distinct().Order());
        Assert.Equal(new[] { false, true }, cells.Select(cell => cell.Lambda).Distinct().Order());
    }

    private static IReadOnlyDictionary<HandoffCell, HandoffResult> HandoffResults()
    {
        var results = new ConcurrentDictionary<HandoffCell, HandoffResult>();
        Parallel.ForEach(HandoffCells(), CellParallelism, cell =>
        {
            var library = Compile(HandoffSource(cell));
            Assert.True(library.Compilation is not null, $"{cell}: {string.Join("; ", library.Errors)}\n{HandoffSource(cell)}");
            Assert.DoesNotContain(library.ExternMembers, id => !id.Contains("Get", StringComparison.Ordinal) && !id.Contains("Take", StringComparison.Ordinal));
            var request = new GenerationRequest(ASSEMBLY, "1.0", "M:Lib.Api.Run(System.Action)", null, null);
            var running = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Run);
            var off = ModelGenerator.Trace(request, library, CancellationToken.None, initializers: TypeInitializerTreatment.Off);
            Assert.Null(running.Answer.Reason);
            Assert.Null(off.Answer.Reason);
            results[cell] = new HandoffResult(running.Answer, off.Answer);
        });
        return results;
    }

    private static string HandoffSource(HandoffCell cell)
    {
        var structure = cell.Channel == Channel.StructReceiver;
        var type = structure ? (cell.Receiver == Receiver.Constrained ? "T" : "S") : "int";
        var modifier = cell.Channel switch { Channel.In => "in", Channel.Out => "out", _ => "ref" };
        var parameter = structure ? "" : modifier + " int x";
        var argument = structure ? "" : modifier + " VALUE";
        var callbackParameter = cell.Lambda ? (structure ? "" : ", ") + "Action callback" : "";
        var callbackArgument = cell.Lambda ? (structure ? "" : ", ") + "() => { }" : "";
        var empty = cell.Channel == Channel.Out ? "x = default;" : "";
        var endpoint = "public static void Take(" + parameter + callbackParameter + ") { " + empty + " }";
        var call = "Take(" + argument + callbackArgument + ");";
        if (cell.Receiver == Receiver.StaticNoBody && !structure)
            endpoint = "public static extern void Take(" + parameter + callbackParameter + ");";
        if (structure)
        {
            endpoint = "";
            call = "VALUE.Take(" + callbackArgument + ");";
        }
        else if (cell.Receiver != Receiver.StaticNoBody && cell.Receiver != Receiver.StaticBody)
        {
            endpoint = "";
            var receiver = cell.Receiver switch
            {
                Receiver.VirtualUnknown or Receiver.InterfaceUnknown => "Get()",
                Receiver.InterfaceKnown => "((ITarget)new Target())",
                Receiver.Constrained => "receiver",
                _ => "new Target()"
            };
            call = receiver + ".Take(" + argument + callbackArgument + ");";
        }
        var helperGeneric = cell.Receiver == Receiver.Constrained ? "<T>" : "";
        var constraint = cell.Receiver == Receiver.Constrained ? " where T : struct, ITarget" : "";
        var helperReceiver = cell.Receiver == Receiver.Constrained ? "T receiver, " : "";
        var passReceiver = cell.Receiver == Receiver.Constrained ? "receiver, " : "";
        // A constrained struct-receiver call stays in a generic body; its static is still named directly there.
        var entry = cell.Receiver == Receiver.Constrained ? "Send(new Target(new object()));" : "Send();";
        var hand = call.Replace("VALUE", cell.Helpers == 0 ? (structure && cell.Receiver == Receiver.Constrained ? "Storage<T>.X" : "State.X") : "x");
        var helpers = "";
        for (var i = cell.Helpers; i > 0; i--)
        {
            helpers += "static void H" + i + helperGeneric + "(" + helperReceiver + modifier + " " + type + " x)" + constraint + " { " + hand + " } ";
            hand = "H" + i + "(" + passReceiver + modifier + " " + (i == 1 ? (structure && cell.Receiver == Receiver.Constrained ? "Storage<T>.X" : "State.X") : "x") + ");";
        }
        var virtualModifier = cell.Receiver is Receiver.VirtualUnknown or Receiver.VirtualKnown ? "virtual " : "";
        var targetStruct = cell.Receiver == Receiver.Constrained;
        var targetDeclaration = "public " + (targetStruct ? "struct" : "class") + " Target : ITarget { public " + virtualModifier +
            "void Take(" + parameter + callbackParameter + ") { " + empty + " } " +
            (targetStruct ? "public object Value; public Target(object value) { Value = value; } " : "") + "}";
        if (structure)
            targetDeclaration = "public struct Target : ITarget { public object Value; public Target(object value) { Value = value; } public void Take(" + callbackParameter + ") { } }";
        return $$"""
            using System;
            namespace Lib {
            public interface ITarget { void Take({{parameter}}{{callbackParameter}}); }
            {{targetDeclaration}}
            public struct S { public int Value; public {{(cell.Receiver == Receiver.StaticNoBody ? "extern void Take(" + callbackParameter.TrimStart(',', ' ') + ");" : "void Take(" + callbackParameter.TrimStart(',', ' ') + ") { }")}} }
            public static class Storage<T> where T : struct, ITarget { public static T X; }
            public static class State { public static {{(structure ? "S" : "int")}} X; }
            public static class Api {
                public static extern {{(cell.Receiver == Receiver.InterfaceUnknown ? "ITarget" : "Target")}} Get();
                static Api() { {{(targetStruct ? "" : "_ = new Target();")}} {{entry}} }
                {{endpoint}}
                {{helpers}}
                static void Send{{helperGeneric}}({{(targetStruct ? "T receiver" : "")}}){{constraint}} { {{(targetStruct && structure ? "Storage<T>.X = receiver;" : "")}} {{hand}} }
                public static void Run(Action a) => a();
            } }
            """;
    }
}
