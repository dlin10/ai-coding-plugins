using ConcurrencyHunter.Scopes;
using Microsoft.CodeAnalysis;

namespace ConcurrencyHunter.Providers;

public sealed record ScopedProjects(ProcessScope Scope, IReadOnlyList<Project> Projects);

public sealed record ProcessScopeDiscovery(IReadOnlyList<ScopedProjects> Scopes, IReadOnlyList<string> Diagnostics);

/// <summary>Divides a solution into process scopes (ADR 0005). The MSBuild workspace already leaves out project
/// references with <c>ReferenceOutputAssembly=false</c>, so every project reference here loads its assembly.</summary>
public static class ProcessScopes
{
    private static readonly HashSet<string> TestFrameworkAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.VisualStudio.TestPlatform.ObjectModel",
        "Microsoft.Testing.Platform",
        "xunit.core",
        "xunit.v3.core",
        "nunit.framework",
        "Microsoft.VisualStudio.TestPlatform.TestFramework"
    };

    public static ProcessScopeDiscovery Discover(Solution solution, string rootDirectory)
    {
        var projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp).ToArray();
        var executables = projects.Where(project => IsExecutable(project) && !IsTestProject(project)).ToArray();
        if (executables.Length == 0)
        {
            var scope = new ProcessScope(ProcessScope.SOLUTION_SCOPE_ID, null, Names(projects));
            return new ProcessScopeDiscovery([new ScopedProjects(scope, Ordered(projects))], [ProcessScope.NO_EXECUTABLE_DIAGNOSTIC]);
        }

        var sharedAssemblyNames = executables.GroupBy(project => project.AssemblyName, StringComparer.Ordinal)
                                             .Where(group => group.Count() > 1)
                                             .Select(group => group.Key)
                                             .ToHashSet(StringComparer.Ordinal);
        var scopes = executables.Select(executable =>
                                {
                                    var closure = Ordered(Closure(solution, executable));
                                    var id = sharedAssemblyNames.Contains(executable.AssemblyName)
                                        ? $"{executable.AssemblyName}@{RelativeProjectPath(executable, rootDirectory)}"
                                        : executable.AssemblyName;
                                    return new ScopedProjects(new ProcessScope(id, executable.Name, Names(closure)), closure);
                                })
                                .OrderBy(scoped => scoped.Scope.Id, StringComparer.Ordinal)
                                .ToArray();
        return new ProcessScopeDiscovery(scopes, []);
    }

    public static bool IsExecutable(Project project) =>
        project.CompilationOptions?.OutputKind is OutputKind.ConsoleApplication or OutputKind.WindowsApplication
                                                  or OutputKind.WindowsRuntimeApplication;

    public static bool IsTestProject(Project project) =>
        project.MetadataReferences.OfType<PortableExecutableReference>()
               .Any(reference => TestFrameworkAssemblies.Contains(Path.GetFileNameWithoutExtension(reference.FilePath ?? reference.Display ?? "")));

    private static IReadOnlyList<Project> Closure(Solution solution, Project executable)
    {
        var closure = new Dictionary<ProjectId, Project>();
        var pending = new Stack<Project>([executable]);
        while (pending.TryPop(out var project))
        {
            if (project.Language != LanguageNames.CSharp || !closure.TryAdd(project.Id, project))
                continue;
            foreach (var reference in project.ProjectReferences)
            {
                if (solution.GetProject(reference.ProjectId) is { } referenced)
                    pending.Push(referenced);
            }
        }

        return closure.Values.ToArray();
    }

    private static Project[] Ordered(IEnumerable<Project> projects) =>
        projects.OrderBy(project => project.Name, StringComparer.Ordinal)
                .ThenBy(project => project.FilePath, StringComparer.Ordinal)
                .ToArray();

    private static string[] Names(IEnumerable<Project> projects) => Ordered(projects).Select(project => project.Name).ToArray();

    private static string RelativeProjectPath(Project project, string rootDirectory) =>
        project.FilePath is null
            ? project.Name
            : Path.GetRelativePath(Path.GetFullPath(rootDirectory), Path.GetFullPath(project.FilePath)).Replace('\\', '/');
}
