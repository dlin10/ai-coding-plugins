using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;
using static ConcurrencyHunter.Core.Tests.Engine.StructPlaceMatrix;

namespace ConcurrencyHunter.Core.Tests.Engine;

/// <summary>The struct-place matrix (<see cref="StructPlaceMatrix"/>) over the storages a field keeps, the box, the local, and the fields
/// whose contents a <c>Nullable</c> or a pair hands out. No span or <c>foreach</c> is over them, so the half meets every route but the
/// two over a span, every use but a <c>foreach</c>, and every form.</summary>
/// <param name="cache">The class's cache, which keeps the runs of its half.</param>
public sealed class StructPlaceMatrixTests(ClassCache cache) : IClassFixture<ClassCache>
{
    private StructPlaceMatrix Matrix => new(cache, FIELD_HELD, Enum.GetValues<Route>().Except([Route.Span, Route.RefForeach]).ToArray(),
                                            Enum.GetValues<Use>().Except([Use.Foreach]).ToArray(), Enum.GetValues<Form>());

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
