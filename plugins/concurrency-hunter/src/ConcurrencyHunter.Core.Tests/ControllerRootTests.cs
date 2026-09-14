using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class ControllerRootTests
{
    private const string RootDirectory = @"C:\fixture";

    [Fact]
    public async Task Public_instance_action_of_a_ControllerBase_descendant_is_a_root()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public sealed class HomeController : ControllerBase
            {
                public void Index() { }
            }
            """);

        var root = Assert.Single(inventory.Roots);
        Assert.Equal("Demo.HomeController.Index()", root.Symbol);
        Assert.Equal("ControllerBase action Demo.HomeController.Index()", root.Display);
        Assert.StartsWith("aspnetcore-action:Fixture:M:Demo.HomeController.Index", root.RootId);
    }

    [Fact]
    public async Task Class_deriving_through_an_intermediate_base_controller_is_discovered()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public abstract class ApplicationController : ControllerBase { }
            public sealed class HomeController : ApplicationController
            {
                public void Index() { }
            }
            """);

        Assert.Equal("Demo.HomeController.Index()", Assert.Single(inventory.Roots).Symbol);
    }

    [Fact]
    public async Task Abstract_controller_contributes_no_roots()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public abstract class HomeController : ControllerBase
            {
                public void Index() { }
            }
            """);

        Assert.Empty(inventory.Roots);
    }

    [Fact]
    public async Task NonAction_method_is_not_a_root()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class HomeController : ControllerBase
            {
                [NonAction]
                public void Helper() { }
            }
            """);

        Assert.Empty(inventory.Roots);
    }

    [Fact]
    public async Task Static_private_protected_and_internal_methods_are_not_roots()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class HomeController : ControllerBase
            {
                public void Action() { }
                public static void StaticMethod() { }
                private void PrivateMethod() { }
                protected void ProtectedMethod() { }
                internal void InternalMethod() { }
            }
            """);

        Assert.Equal("HomeController.Action()", Assert.Single(inventory.Roots).Symbol);
    }

    [Fact]
    public async Task Class_with_a_Controller_suffix_that_does_not_derive_from_ControllerBase_contributes_no_roots()
    {
        var inventory = await Analyze("""
            public sealed class HomeController
            {
                public void Index() { }
            }
            """);

        Assert.Empty(inventory.Roots);
    }

    [Fact]
    public async Task Public_method_declared_on_a_base_controller_is_a_root_of_the_base_only()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public class BaseController : ControllerBase
            {
                public void BaseAction() { }
            }
            public sealed class DerivedController : BaseController
            {
                public void DerivedAction() { }
            }
            """);

        Assert.Equal(
            ["Demo.BaseController.BaseAction()", "Demo.DerivedController.DerivedAction()"],
            inventory.Roots.Select(root => root.Symbol));
    }

    [Fact]
    public async Task Generic_controller_contributes_no_roots()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class HomeController<T> : ControllerBase
            {
                public void Index() { }
            }
            """);

        Assert.Empty(inventory.Roots);
    }

    [Fact]
    public async Task Overloads_whose_parameter_types_share_a_simple_name_get_distinct_root_ids()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            namespace A { public sealed class Payload { } }
            namespace B { public sealed class Payload { } }
            public sealed class PayloadController : ControllerBase
            {
                public void Post(A.Payload payload) { }
                public void Post(B.Payload payload) { }
            }
            """);

        Assert.Equal(2, inventory.Roots.Count);
        Assert.Single(inventory.Roots.Select(root => root.Symbol).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, inventory.Roots.Select(root => root.RootId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(inventory.Roots, root => root.RootId.Contains("A.Payload", StringComparison.Ordinal));
        Assert.Contains(inventory.Roots, root => root.RootId.Contains("B.Payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_named_controllers_in_two_projects_get_distinct_root_ids()
    {
        const string source = """
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            public sealed class HomeController : ControllerBase
            {
                public void Index() { }
            }
            """;
        var solution = FixtureSolution.CreateProjects(
            ("First", "HomeController.cs", source),
            ("Second", "HomeController.cs", source));

        var inventory = await PhaseOneAnalyzer.CollectAsync(solution, RootDirectory, CancellationToken.None);

        Assert.Equal(2, inventory.Roots.Count);
        Assert.Single(inventory.Roots.Select(root => root.Symbol).Distinct(StringComparer.Ordinal));
        Assert.Equal(2, inventory.Roots.Select(root => root.RootId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(inventory.Roots, root => root.RootId.StartsWith("aspnetcore-action:First:", StringComparison.Ordinal));
        Assert.Contains(inventory.Roots, root => root.RootId.StartsWith("aspnetcore-action:Second:", StringComparison.Ordinal));
    }

    private static Task<AccessInventory> Analyze(string source) =>
        PhaseOneAnalyzer.CollectAsync(
            FixtureSolution.Create(("Controller.cs", source)),
            RootDirectory,
            CancellationToken.None);
}
