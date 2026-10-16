using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.StructPlaceMatrix;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The struct-place matrix (<see cref="StructPlaceMatrix"/>) over an array cell, a capture cell, a by-value parameter, a
/// call's result and a captured parameter. None of them is declared <c>readonly</c>, so the half meets every route, every use and every
/// form but readonly storage.</summary>
/// <param name="cache">The class's cache, which keeps the runs of its half.</param>
public sealed class StructPlaceMatrixCellTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private StructPlaceMatrix Matrix => new(cache, CELLS, Enum.GetValues<Route>(), Enum.GetValues<Use>(), [Form.Plain, Form.ReadonlyReference]);

    [Fact]
    public void Every_cell_accesses_as_expected() => Matrix.Every_cell_accesses_as_expected();

    [Fact]
    public void No_cell_loses_an_access() => Matrix.No_cell_loses_an_access();

    [Fact]
    public void No_copy_is_a_resource() => Matrix.No_copy_is_a_resource();

    [Fact]
    public void No_copy_loses_the_object_its_reference_field_holds() => Matrix.No_copy_loses_the_object_its_reference_field_holds();

    [Fact]
    public void No_method_group_delegate_writes_the_storage() => Matrix.No_method_group_delegate_writes_the_storage();

    [Fact]
    public void Event_add_on_contents_and_call_results_is_a_cell() => Matrix.Event_add_on_contents_and_call_results_is_a_cell();

    [Fact]
    public void Every_value_of_every_axis_occurs() => Matrix.Every_value_of_every_axis_occurs();
}
