using System.Text;
using System.Text.Json;
using Common.Mcp;
using ConcurrencyHunter.Core.Tests.Fixtures;
using ConcurrencyHunter.Providers.LibraryModels;
using ConcurrencyHunter.Providers.LibraryModels.Generation;
using Xunit;

namespace ConcurrencyHunter.Cli.Tests;

/// <summary><c>generate</c>: its options and usage errors (G-6), and the answers it writes.</summary>
public sealed class GenerateCommandTests : IDisposable
{
    private const string ALL = "M:System.Linq.Enumerable.All``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";
    private const string LONG_COUNT = "M:System.Linq.Enumerable.LongCount``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";
    private const string USAGE = "Usage: concurrency-hunter generate";

    private readonly GenerationInstall _install = new();
    private readonly string _out;

    public GenerateCommandTests() => _out = Path.Combine(_install.Root, "answer.json");

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Unknown_option_is_a_usage_error_and_writes_no_file() =>
        AssertUsageError([.. Valid(), "--target", "x"], "Unexpected argument '--target'.");

    // As metrics's parser does, the message names the repeated option's value.
    [Fact]
    public void Duplicate_option_is_a_usage_error_and_writes_no_file() =>
        AssertUsageError([.. Valid(), "--assembly", "System.Text.Json"], "Unexpected argument 'System.Text.Json'.");

    [Theory]
    [InlineData("--assembly")]
    [InlineData("--version")]
    [InlineData("--member")]
    [InlineData("--out")]
    public void Missing_option_is_a_usage_error_and_writes_no_file(string option) =>
        AssertUsageError(Without(Valid(), option), $"{option} is required.");

    [Fact]
    public void Option_without_its_value_is_a_usage_error_and_writes_no_file() =>
        AssertUsageError([.. Valid(), "--framework"], "Unexpected argument '--framework'.");

    [Theory]
    [InlineData("net8")]
    [InlineData("netcoreapp3.1")]
    [InlineData("netstandard2.0")]
    [InlineData("net8.0-windows")]
    [InlineData("NET8.0")]
    [InlineData("net0.0")]
    public void Framework_that_is_not_net_major_is_a_usage_error_and_writes_no_file(string framework) =>
        AssertUsageError([.. Valid(), "--framework", framework], $"--framework '{framework}' is not net<major>.0.");

    [Theory]
    [InlineData("8.x")]
    [InlineData("v8")]
    [InlineData("8.0.0.0.0")]
    [InlineData("8.0-preview")]
    [InlineData("70000")]
    public void Unparsable_framework_version_is_a_usage_error_and_writes_no_file(string version) =>
        AssertUsageError(With(Valid(), "--version", version), $"--version '{version}' is not an assembly version.");

    [Theory]
    [InlineData("1..2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("latest")]
    [InlineData("[1.0,2.0)")]
    [InlineData("1.0+")]
    [InlineData("1.0+?")]
    [InlineData("1.0.0-01")]
    public void Unparsable_package_version_is_a_usage_error_and_writes_no_file(string version) =>
        AssertUsageError([.. With(Valid(), "--version", version), "--package", "Polly"], $"--version '{version}' is not a package version.");

    [Theory]
    [InlineData("System.Linq.Enumerable.All")]
    [InlineData("X:System.Linq.Enumerable.All")]
    [InlineData("M:System.Linq.Enumerable.All(")]
    [InlineData("M:System.Linq.Enumerable.All(*)")]
    [InlineData("M:")]
    [InlineData("M:System.Linq.Enumerable.All``0(System.Int32)")]
    public void Member_that_is_not_a_declaration_id_is_a_usage_error_and_writes_no_file(string member) =>
        AssertUsageError(With(Valid(), "--member", member), $"--member '{member}' is not a declaration id.");

    [Theory]
    [InlineData("System/Linq")]
    [InlineData(@"System\Linq")]
    [InlineData("C:System.Linq")]
    [InlineData("..")]
    [InlineData("../System.Linq")]
    [InlineData("System..Linq")]
    [InlineData("System.Linq.")]
    public void Assembly_outside_the_name_grammar_is_a_usage_error_and_writes_no_file(string assembly) =>
        AssertUsageError(With(Valid(), "--assembly", assembly), $"--assembly '{assembly}' is not an assembly name.");

    [Theory]
    [InlineData("Polly/Extensions")]
    [InlineData(@"Polly\..\Polly")]
    [InlineData("..")]
    [InlineData(".Polly")]
    [InlineData("C:Polly")]
    public void Package_outside_the_id_grammar_is_a_usage_error_and_writes_no_file(string package) =>
        AssertUsageError([.. With(Valid(), "--version", "7.2.3"), "--package", package], $"--package '{package}' is not a package id.");

    [Fact]
    public void Package_missing_from_the_packages_folder_is_answered_with_its_reason()
    {
        _install.SharedFramework("8.0.5", runtime: true);

        var (code, error) = Run(["--assembly", "Acme.Lib", "--version", "1.2.3", "--package", "Acme.Lib", "--framework", "net8.0", "--member",
                                 "M:Acme.Api.Run", "--out", _out]);

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("classified").ValueKind);
        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, document.RootElement.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("generation").GetProperty("implementation").ValueKind);
        Assert.Equal("Acme.Lib", document.RootElement.GetProperty("assembly").GetProperty("package").GetString());
        // The platform was chosen before the package was looked for, so the record names it.
        Assert.Equal("net8.0", document.RootElement.GetProperty("generation").GetProperty("framework").GetString());
    }

    [Fact]
    public void Framework_member_of_an_uninstalled_major_names_the_major_it_chose()
    {
        _install.SharedFramework("8.0.5", runtime: true);

        var (code, error) = Run(["--assembly", "System.Linq", "--version", "9.0", "--member", ALL, "--out", _out]);

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        Assert.Equal(GenerationReasons.NO_IMPLEMENTATION, document.RootElement.GetProperty("reason").GetString());
        Assert.Equal("net9.0", document.RootElement.GetProperty("generation").GetProperty("framework").GetString());
    }

    [Fact]
    public void Package_reaches_the_package_path_the_resolver_chose()
    {
        _install.SharedFramework("8.0.5", runtime: true);
        var package = _install.Package("Acme.Lib", "1.2.3", "", "netstandard2.0/Acme.Lib", "net8.0/Acme.Lib", "net9.0/Acme.Lib");

        var (code, error) = Run(["--assembly", "Acme.Lib", "--version", "1.2.3.0", "--package", "acme.lib", "--framework", "net8.0", "--member",
                                 "M:Acme.Api.Run", "--out", _out]);

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        var generation = document.RootElement.GetProperty("generation");
        Assert.Equal(Path.Combine(package, "lib", "net8.0", "Acme.Lib.dll"), generation.GetProperty("implementation").GetProperty("path").GetString());
        Assert.Equal("net8.0", generation.GetProperty("framework").GetString());
        // The marker library declares no Acme.Api.Run: the member check runs on the compiled package assembly.
        Assert.Equal(GenerationReasons.MEMBER_NOT_FOUND, document.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Property_id_reaches_the_generator_and_is_classified_as_an_accessor()
    {
        _install.SharedFramework("8.0.5", runtime: true);
        var lib = Directory.CreateDirectory(Path.Combine(_install.Packages, "acme.props", "2.0.0", "lib", "net8.0")).FullName;
        EmittedAssemblies.Write(Path.Combine(lib, "Acme.Props.dll"), "Acme.Props",
                                "namespace Acme { public sealed class Box { public System.Func<int> Value { get; set; } } }");

        var (code, error) = Run(["--assembly", "Acme.Props", "--version", "2.0.0", "--package", "Acme.Props", "--framework", "net8.0", "--member",
                                 "P:Acme.Box.Value", "--out", _out]);

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        Assert.Equal(GenerationReasons.ACCESSOR, document.RootElement.GetProperty("reason").GetString());
        // The document carries no detail: the generator's answer names the accessors to generate instead.
        var answer = ModelGenerator.Generate(new GenerationRequest("Acme.Props", "2.0.0", "P:Acme.Box.Value", "Acme.Props", "net8.0"), _install.Resolver(),
                                             CancellationToken.None);
        Assert.Contains("M:Acme.Box.get_Value", answer.Detail);
        Assert.Contains("M:Acme.Box.set_Value(System.Func{System.Int32})", answer.Detail);
    }

    [Fact]
    public void Installed_runtime_All_is_classified_invoke_now()
    {
        var (code, error) = Run(["--assembly", "System.Linq", "--version", Environment.Version.Major.ToString(), "--member", ALL, "--out", _out],
                                ImplementationAssemblies.ForThisProcess());

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        var root = document.RootElement;
        Assert.Equal(ALL, root.GetProperty("member").GetString());
        Assert.Equal("invoke-now", root.GetProperty("classified").GetProperty("predicate").GetProperty("fate").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("classified").GetProperty("predicate").GetProperty("holder").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
        // On .NET 10 All reads its source through TryGetSpan(source, out span), and an element of that span has no name.
        Assert.Equal(GenerationReasons.VOCABULARY, root.GetProperty("modelReason").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("model").ValueKind);
        Assert.NotEqual(0, root.GetProperty("causes").GetArrayLength());
    }

    [Fact]
    public void Installed_runtime_LongCount_writes_an_entry_the_project_reader_accepts()
    {
        var (code, error) = Run(["--assembly", "System.Linq", "--version", Environment.Version.Major.ToString(), "--member", LONG_COUNT, "--out", _out],
                                ImplementationAssemblies.ForThisProcess());

        Assert.True(code == ExitCode.Ok, error);
        using var document = Answer();
        var root = document.RootElement;
        Assert.Equal(LONG_COUNT, root.GetProperty("member").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("modelReason").ValueKind);
        Assert.Equal(0, root.GetProperty("causes").GetArrayLength());
        var modelFile = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"models\":[" + root.GetProperty("model").GetRawText() + "]}");
        var models = ProjectModelFiles.Read("generated.json", modelFile);
        Assert.Empty(models.Rejections);
        Assert.Single(models.Entries);
        Assert.Equal($"net{Environment.Version.Major}.0", root.GetProperty("generation").GetProperty("framework").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("generation").GetProperty("implementation").GetProperty("mvid").GetString()));
    }

    private string[] Valid() => ["--assembly", "System.Linq", "--version", "8.0", "--member", ALL, "--out", _out];

    private static string[] With(string[] args, string option, string value)
    {
        var copy = args.ToArray();
        copy[Array.IndexOf(copy, option) + 1] = value;
        return copy;
    }

    private static string[] Without(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return [.. args[..index], .. args[(index + 2)..]];
    }

    private (int Code, string Error) Run(string[] args, ImplementationAssemblies? resolver = null)
    {
        using var error = new StringWriter();
        var code = GenerateCommand.Run(args, error, resolver ?? _install.Resolver());
        return (code, error.ToString());
    }

    private void AssertUsageError(string[] args, string message)
    {
        var (code, error) = Run(args);

        Assert.Equal(ExitCode.UsageError, code);
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Contains(USAGE, error, StringComparison.Ordinal);
        Assert.False(File.Exists(_out), "A usage error wrote the output file.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_install.Root));
    }

    private JsonDocument Answer()
    {
        Assert.True(File.Exists(_out), "The command wrote no output file.");
        return JsonDocument.Parse(File.ReadAllBytes(_out));
    }
}
