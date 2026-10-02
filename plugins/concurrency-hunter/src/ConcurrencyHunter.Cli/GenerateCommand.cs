using Common.Mcp;
using ConcurrencyHunter.Providers.LibraryModels.Generation;

namespace ConcurrencyHunter;

/// <summary>
/// <c>generate</c> (SPEC TD-034b, R1): runs the model generator for one library member and writes its answer (G-6) to the output
/// file — the one file it writes. A usage error writes nothing.
/// </summary>
internal static class GenerateCommand
{
    private const string USAGE = "Usage: concurrency-hunter generate --assembly <name> --version <version> --member <declaration id> " +
                                 "[--package <id>] [--framework netX.0] --out <file>";

    /// <summary>Runs the command.</summary>
    /// <param name="args">The arguments after <c>generate</c>.</param>
    /// <param name="error">Where usage errors and failures are written.</param>
    /// <param name="resolver">Finds the implementation assembly; this process's when <c>null</c>. A parameter so tests can resolve
    /// against a fake install.</param>
    internal static int Run(string[] args, TextWriter error, ImplementationAssemblies? resolver = null)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine(USAGE);
            return ExitCode.UsageError;
        }

        try
        {
            var answer = ModelGenerator.Generate(new GenerationRequest(options.Assembly, options.Version, options.Member, options.Package, options.Framework),
                                                 resolver ?? ImplementationAssemblies.ForThisProcess(), CancellationToken.None);
            File.WriteAllBytes(Path.GetFullPath(options.Out), GeneratedDocument.Write(answer));
            return ExitCode.Ok;
        }
        catch (Exception exception)
        {
            error.WriteLine(exception.Message);
            return ExitCode.Failure;
        }
    }

    private sealed record Options(string Assembly, string Version, string Member, string? Package, string? Framework, string Out)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] is not ("--assembly" or "--version" or "--member" or "--package" or "--framework" or "--out") || index + 1 >= args.Length ||
                    !values.TryAdd(args[index], args[++index]))
                    throw new ArgumentException($"Unexpected argument '{args[index]}'.");
            }

            var assembly = Required(values, "--assembly");
            var version = Required(values, "--version");
            var member = Required(values, "--member");
            var output = Required(values, "--out");
            var package = values.GetValueOrDefault("--package");
            var framework = values.GetValueOrDefault("--framework");
            if (!ImplementationAssemblies.IsAssemblyName(assembly))
                throw new ArgumentException($"--assembly '{assembly}' is not an assembly name.");
            if (package is not null && !ImplementationAssemblies.IsPackageId(package))
                throw new ArgumentException($"--package '{package}' is not a package id.");
            if (framework is not null && !ImplementationAssemblies.TryParseFramework(framework, out _))
                throw new ArgumentException($"--framework '{framework}' is not net<major>.0.");
            if (package is null ? !ImplementationAssemblies.TryParseAssemblyMajor(version, out _) : !PackageVersion.TryParse(version, out _))
                throw new ArgumentException($"--version '{version}' is not {(package is null ? "an assembly version" : "a package version")}.");
            if (!ModelGenerator.IsMemberId(member))
                throw new ArgumentException($"--member '{member}' is not a declaration id.");
            return new Options(assembly, version, member, package, framework, output);
        }

        private static string Required(Dictionary<string, string> values, string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"{name} is required.");
    }
}
