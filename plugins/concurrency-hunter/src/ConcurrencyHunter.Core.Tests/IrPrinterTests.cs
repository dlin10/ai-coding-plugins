using System.Globalization;
using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Ir;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class IrPrinterTests
{
    private static readonly IrProvenance Provenance = new(
        new SourceSpan("Fixture.cs", 10, 2, 10, 8),
        "Fixture.Method()",
        "InvocationExpression",
        "direct");

    private static readonly IrFieldRef Field = new(
        "Fixture",
        "Fixture.State",
        "Value",
        IrFieldKind.Field,
        false,
        false,
        "System.Int32");

    [Fact]
    public void Every_operation_kind_has_a_text_form()
    {
        var predecessor = new IrFlowPredecessor(0, IrEdgeKind.Explicit);
        var operations = new List<IrOperation>
        {
            new IrAssignOperation(0, 1, 0, Provenance),
            new IrPhiOperation(1, 2, [new IrPhiInput(predecessor, 1)], Provenance),
            new IrAllocateOperation(2, 3, "Fixture.State", Provenance),
            new IrLoadFieldOperation(3, 4, 3, Field, Provenance),
            new IrStoreFieldOperation(4, 3, Field, 4, 3, Provenance),
            new IrLoadElementOperation(5, 5, 3, [0], Provenance),
            new IrStoreElementOperation(6, 3, [0], 5, Provenance)
        };
        var nextOperation = 7;
        var nextValue = 6;
        foreach (var callKind in Enum.GetValues<IrCallKind>())
        {
            operations.Add(new IrCallOperation(
                nextOperation++, nextValue++, callKind, "Fixture.Call()", 3, [0], Provenance));
        }

        operations.AddRange(
        [
            new IrCreateDelegateOperation(nextOperation++, nextValue++, "body:target", null, 3, Provenance),
            new IrCaptureOperation(nextOperation++, 0, "body:target", Provenance),
            new IrEscapeOperation(nextOperation++, 3, "return", Provenance),
            new IrReturnOperation(nextOperation++, 0, Provenance),
            new IrAwaitOperation(nextOperation++, nextValue++, 0, Provenance),
            new IrSpawnOperation(nextOperation++, nextValue++, 0, Provenance),
            new IrJoinOperation(nextOperation++, nextValue - 1, Provenance),
            new IrAcquireOperation(nextOperation++, 3, IrSynchronizationPrimitive.Monitor,
                                   IrLockMode.Exclusive, Provenance),
            new IrReleaseOperation(nextOperation++, 3, IrSynchronizationPrimitive.Monitor,
                                   IrLockMode.Exclusive, Provenance),
            new IrAtomicOperation(nextOperation++, nextValue++, "Interlocked.Exchange", [3, 0], Provenance),
            new IrComputeOperation(nextOperation++, nextValue++, "add", [0, 1], Provenance),
            new IrCompareOperation(nextOperation++, nextValue++, IrComparisonKind.Equality, 0, 1, null, Provenance),
            new IrConvertOperation(nextOperation++, nextValue++, 0, "System.Int64",
                                   IrConversionKind.Numeric, Provenance),
            new IrUnknownOperation(nextOperation, nextValue, "Dynamic", "unresolved", [0], Provenance)
        ]);
        var body = WellFormedBody(operations);

        var text = IrPrinter.Print(body);

        foreach (var form in new[]
                 {
                     "assign", "phi", "allocate", "load-field", "store-field", "load-element",
                     "store-element", "create-delegate", "capture", "escape", "return", "await",
                     "spawn", "join", "acquire", "release", "atomic", "compute", "compare", "convert", "unknown"
                 })
        {
            Assert.Contains($" {form} ", text);
        }

        foreach (var callKind in Enum.GetValues<IrCallKind>())
            Assert.Contains($"call {callKind}", text);
    }

    [Fact]
    public void Printing_is_independent_of_culture()
    {
        var body = WellFormedBody() with
        {
            Values = [new IrValue(1234, IrValueKind.Constant, "System.Int32", "1234", 5678)]
        };
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var french = IrPrinter.Print(body);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");

            Assert.Equal(french, IrPrinter.Print(body));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Validator_accepts_a_well_formed_body()
    {
        Assert.Empty(IrValidator.Validate(WellFormedBody()));
    }

    [Fact]
    public void Validator_reports_a_value_defined_twice_an_unknown_operand_and_a_phi_arity_mismatch()
    {
        var body = WellFormedBody();
        var blocks = body.Blocks.ToArray();
        blocks[0] = blocks[0] with
        {
            Operations =
            [
                new IrAssignOperation(10, 3, 99, Provenance),
                new IrAssignOperation(11, 3, 0, Provenance)
            ]
        };
        blocks[1] = blocks[1] with
        {
            Operations = [new IrPhiOperation(12, 1, [], Provenance)]
        };

        var problems = IrValidator.Validate(body with { Blocks = blocks });

        Assert.Contains(problems, problem => problem.Contains("defined twice", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("unknown operand %99", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("0 inputs for 1 flow predecessors", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reports_a_wrong_schema_version()
    {
        var problems = IrValidator.Validate(WellFormedBody() with { SchemaVersion = "2.0" });

        Assert.Contains(problems, problem => problem.Contains("expected '1.1'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reports_non_contiguous_and_misplaced_entry_exit_blocks()
    {
        var body = WellFormedBody();
        var blocks = body.Blocks.ToArray();
        blocks[0] = blocks[0] with { Ordinal = 1, Kind = IrBlockKind.Block };
        blocks[^1] = blocks[^1] with { Ordinal = 4, Kind = IrBlockKind.Block };

        var problems = IrValidator.Validate(body with { Blocks = blocks });

        Assert.Contains("Block ordinals are not contiguous from zero.", problems);
        Assert.Contains("The first block is not Entry.", problems);
        Assert.Contains("The last block is not Exit.", problems);
    }

    [Fact]
    public void Validator_reports_a_branch_outside_the_body()
    {
        var body = WellFormedBody();
        var blocks = body.Blocks.ToArray();
        blocks[0] = blocks[0] with
        {
            FallThroughBranch = blocks[0].FallThroughBranch! with { Destination = 99 }
        };

        var problems = IrValidator.Validate(body with { Blocks = blocks });

        Assert.Contains(problems, problem => problem.Contains("b99", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reports_a_phi_after_another_operation()
    {
        var body = WellFormedBody();
        var blocks = body.Blocks.ToArray();
        var predecessor = blocks[1].FlowPredecessors[0];
        blocks[1] = blocks[1] with
        {
            Operations =
            [
                new IrAssignOperation(20, 2, 0, Provenance),
                new IrPhiOperation(21, 1, [new IrPhiInput(predecessor, 0)], Provenance)
            ]
        };

        var problems = IrValidator.Validate(body with { Blocks = blocks });

        Assert.Contains(problems, problem => problem.Contains("not at the start", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_reports_a_region_outside_its_parent()
    {
        var body = WellFormedBody() with
        {
            Regions =
            [
                new IrRegion(0, IrRegionKind.Root, null, 0, 2, null),
                new IrRegion(1, IrRegionKind.Try, 0, 1, 3, null)
            ]
        };

        var problems = IrValidator.Validate(body);

        Assert.Contains("Region r1 is outside parent r0.", problems);
    }

    [Fact]
    public void Validator_reports_flow_predecessors_that_are_not_totally_ordered()
    {
        var body = WellFormedBody();
        var blocks = body.Blocks.ToArray();
        blocks[1] = blocks[1] with
        {
            FlowPredecessors =
            [
                new IrFlowPredecessor(1, IrEdgeKind.Explicit),
                new IrFlowPredecessor(0, IrEdgeKind.Exceptional)
            ],
            Operations = []
        };

        var problems = IrValidator.Validate(body with { Blocks = blocks });

        Assert.Contains(problems, problem => problem.Contains("not distinct and ordered", StringComparison.Ordinal));
    }

    [Fact]
    public void Printer_orders_values_regions_and_blocks_and_keeps_provenance_on_operation_lines()
    {
        var body = WellFormedBody();
        body = body with
        {
            Values = body.Values.Reverse().ToArray(),
            Regions =
            [
                new IrRegion(1, IrRegionKind.Try, 0, 1, 1, null),
                body.Regions[0]
            ],
            Blocks = body.Blocks.Reverse().ToArray()
        };

        var lines = IrPrinter.Print(body).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(Array.FindIndex(lines, line => line.StartsWith("value %0", StringComparison.Ordinal)) <
                    Array.FindIndex(lines, line => line.StartsWith("value %1", StringComparison.Ordinal)));
        Assert.True(Array.FindIndex(lines, line => line.StartsWith("region r0", StringComparison.Ordinal)) <
                    Array.FindIndex(lines, line => line.StartsWith("region r1", StringComparison.Ordinal)));
        Assert.True(Array.FindIndex(lines, line => line.StartsWith("block b0", StringComparison.Ordinal)) <
                    Array.FindIndex(lines, line => line.StartsWith("block b2", StringComparison.Ordinal)));
        Assert.All(lines.Where(line => line.StartsWith("operation ", StringComparison.Ordinal)),
            line => Assert.Contains(" | source=", line));
    }

    private static IrBody WellFormedBody(IReadOnlyList<IrOperation>? operations = null)
    {
        var firstPredecessor = new IrFlowPredecessor(0, IrEdgeKind.Explicit);
        return new IrBody(
            "body:fixture",
            IrBodyKind.Method,
            "Fixture",
            "Fixture.Method()",
            Enumerable.Range(0, 32)
                      .Select(id => new IrValue(id, id == 0 ? IrValueKind.Parameter : IrValueKind.Temporary,
                                                "System.Int32", $"v{id}", id))
                      .ToArray(),
            [
                new IrBlock(
                    0,
                    IrBlockKind.Entry,
                    0,
                    [],
                    [],
                    [],
                    null,
                    new IrBranch(IrBranchKind.Regular, 1, null, null, [], [], [])),
                new IrBlock(
                    1,
                    IrBlockKind.Block,
                    0,
                    [0],
                    [firstPredecessor],
                    operations ?? [new IrPhiOperation(0, 1, [new IrPhiInput(firstPredecessor, 0)], Provenance)],
                    null,
                    new IrBranch(IrBranchKind.Regular, 2, null, null, [], [], [])),
                new IrBlock(
                    2,
                    IrBlockKind.Exit,
                    0,
                    [1],
                    [new IrFlowPredecessor(1, IrEdgeKind.Explicit)],
                    [],
                    null,
                    null)
            ],
            [new IrRegion(0, IrRegionKind.Root, null, 0, 2, null)]);
    }
}
