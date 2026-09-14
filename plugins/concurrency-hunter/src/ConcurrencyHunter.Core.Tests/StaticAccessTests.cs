using ConcurrencyHunter.Analysis;
using ConcurrencyHunter.Core.Tests.Fixtures;
using Xunit;

namespace ConcurrencyHunter.Core.Tests;

public sealed class StaticAccessTests
{
    private const string RootDirectory = @"C:\fixture";

    [Fact]
    public async Task Assignment_to_a_static_field_is_a_write()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Set() { _value = 1; }
            }
            """, "_value");

        Assert.Equal(AccessOperation.Write, access.Operation);
    }

    [Fact]
    public async Task Deconstruction_assignment_writes_each_static_target()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _x;
                private static int _y;
                private static int _z;
                public void Set() { (_x, (_y, _z)) = (1, (2, 3)); }
            }
            """);

        Assert.Equal(["_x", "_y", "_z"], inventory.Accesses.Select(access => access.Resource.AccessPath.Single()));
        Assert.All(inventory.Accesses, access => Assert.Equal(AccessOperation.Write, access.Operation));
    }

    [Fact]
    public async Task Reading_a_static_field_is_a_read()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int Get() { return _value; }
            }
            """, "_value");

        Assert.Equal(AccessOperation.Read, access.Operation);
    }

    [Fact]
    public async Task Compound_assignment_is_one_read_modify_write()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Add() { _value += 2; }
            }
            """, "_value");

        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);
    }

    [Fact]
    public async Task Increment_and_decrement_are_read_modify_write()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Change() { _value++; --_value; }
            }
            """);

        Assert.Equal(2, inventory.Accesses.Count);
        Assert.All(inventory.Accesses, access => Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation));
    }

    [Fact]
    public async Task Null_coalescing_assignment_is_read_modify_write()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static string? _value;
                public void Ensure() { _value ??= "value"; }
            }
            """, "_value");

        Assert.Equal(AccessOperation.ReadModifyWrite, access.Operation);
    }

    [Fact]
    public async Task Member_access_through_a_static_field_is_a_read_of_the_field()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class Box { public int Value { get; set; } }
            public sealed class ValuesController : ControllerBase
            {
                private static readonly Box _box = new();
                public void Set(int value) { _box.Value = value; }
            }
            """, "_box");

        Assert.Equal(AccessOperation.Read, access.Operation);
    }

    [Fact]
    public async Task Const_and_instance_fields_produce_no_access()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private const int Constant = 1;
                private int _instance = 2;
                public int Get() { return Constant + _instance; }
            }
            """);

        Assert.Empty(inventory.Accesses);
    }

    [Fact]
    public async Task Ref_out_and_in_arguments_produce_no_access()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public void Pass()
                {
                    UseRef(ref _value);
                    UseOut(out _value);
                    UseIn(in _value);
                }
                private static void UseRef(ref int value) { }
                private static void UseOut(out int value) { value = 0; }
                private static void UseIn(in int value) { }
            }
            """);

        Assert.Empty(inventory.Accesses);
    }

    [Fact]
    public async Task Nameof_produces_no_access()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public string GetName() { return nameof(_value); }
            }
            """);

        Assert.Empty(inventory.Accesses);
    }

    [Fact]
    public async Task Access_inside_a_lambda_or_local_function_belongs_to_the_enclosing_action()
    {
        var inventory = await Analyze("""
            using System;
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int Get()
                {
                    Func<int> read = () => _value;
                    int Local() => _value;
                    return read() + Local();
                }
            }
            """);

        Assert.Equal(2, inventory.Accesses.Count);
        Assert.All(inventory.Accesses, access => Assert.Equal("ValuesController.Get()", access.Root.Symbol));
    }

    [Fact]
    public async Task Method_called_from_an_action_is_not_followed()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static int _value;
                public int Get() { return Helper(); }
                private static int Helper() { return _value; }
            }
            """);

        Assert.Empty(inventory.Accesses);
    }

    [Fact]
    public async Task Symbol_region_path_and_span_use_the_expectation_format()
    {
        var inventory = await Analyze("""
            using System.Threading;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo.Web.Cases;

            public static class Outer
            {
                public sealed class NestedController : ControllerBase
                {
                    private static int _flag;

                    public string? Read(string? value, CancellationToken cancellationToken)
                    {
                        return _flag.ToString();
                    }
                }
            }
            """, "Cases/Nested.cs");

        var access = Assert.Single(inventory.Accesses);
        Assert.Equal("Demo.Web.Cases.Outer.NestedController.Read(string, CancellationToken)", access.Symbol);
        Assert.Equal("Fixture", access.Resource.Assembly);
        Assert.Equal("static:Demo.Web.Cases.Outer.NestedController", access.Resource.Region);
        Assert.Equal(["_flag"], access.Resource.AccessPath);
        Assert.Equal(new SourceSpan("Cases/Nested.cs", 14, 20, 14, 25), access.Source);
    }

    [Fact]
    public async Task Access_inside_a_lock_on_a_static_field_holds_that_protection()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object Gate = new();
                private static int _value;
                public int Get()
                {
                    lock (Gate) { return _value; }
                }
            }
            """, "_value");

        Assert.Equal(["static:ValuesController.Gate"], access.HeldProtection);
        Assert.Equal(["Fixture:static:ValuesController.Gate"], access.HeldProtectionIds);
    }

    [Fact]
    public async Task Lock_on_this_or_an_instance_field_holds_no_protection()
    {
        var inventory = await Analyze("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private readonly object _gate = new();
                private static int _value;
                public void Change()
                {
                    lock (this) { _value++; }
                    lock (_gate) { _value++; }
                }
            }
            """);

        var accesses = inventory.Accesses.Where(access => access.Resource.AccessPath.Single() == "_value").ToArray();
        Assert.Equal(2, accesses.Length);
        Assert.All(accesses, access =>
        {
            Assert.Empty(access.HeldProtection);
            Assert.Empty(access.HeldProtectionIds);
        });
    }

    [Fact]
    public async Task Lock_around_a_lambda_does_not_protect_accesses_inside_the_lambda()
    {
        var access = await OnlyAccess("""
            using System;
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object Gate = new();
                private static int _value;
                public int Get()
                {
                    lock (Gate)
                    {
                        Func<int> read = () => _value;
                        return read();
                    }
                }
            }
            """, "_value");

        Assert.Empty(access.HeldProtection);
        Assert.Empty(access.HeldProtectionIds);
    }

    [Fact]
    public async Task Nested_locks_hold_both_protections()
    {
        var access = await OnlyAccess("""
            using Microsoft.AspNetCore.Mvc;
            public sealed class ValuesController : ControllerBase
            {
                private static readonly object First = new();
                private static readonly object Second = new();
                private static int _value;
                public int Get()
                {
                    lock (Second)
                    lock (First)
                    {
                        return _value;
                    }
                }
            }
            """, "_value");

        Assert.Equal(
            ["static:ValuesController.First", "static:ValuesController.Second"],
            access.HeldProtection);
        Assert.Equal(
            ["Fixture:static:ValuesController.First", "Fixture:static:ValuesController.Second"],
            access.HeldProtectionIds);
    }

    private static async Task<StaticAccess> OnlyAccess(string source, string fieldName,
                                                       string path = "Controller.cs")
    {
        var inventory = await Analyze(source, path);
        return Assert.Single(inventory.Accesses, access =>
            access.Resource.AccessPath.Count == 1 && access.Resource.AccessPath[0] == fieldName);
    }

    private static Task<AccessInventory> Analyze(string source, string path = "Controller.cs") =>
        PhaseOneAnalyzer.CollectAsync(
            FixtureSolution.Create((path, source)),
            RootDirectory,
            CancellationToken.None);
}
