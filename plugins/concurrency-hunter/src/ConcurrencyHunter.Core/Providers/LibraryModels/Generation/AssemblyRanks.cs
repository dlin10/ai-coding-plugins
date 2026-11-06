namespace ConcurrencyHunter.Providers.LibraryModels.Generation;

/// <summary>The rank of a generation target's implementation assembly among the assemblies it is compiled against: 0 for an assembly that
/// references none of them, otherwise one more than the highest rank of an assembly it references. An assembly therefore ranks above every
/// assembly its references reach, directly or through others, and a member's dependency, which is always in such an assembly, ranks below
/// it. Assemblies whose references lead back to one another share one rank. Each request is resolved once, without its member; a request
/// the resolver opens no implementation for ranks 0. One caller at a time.</summary>
/// <param name="resolver">Finds a request's implementation and the files it is compiled against.</param>
internal sealed class AssemblyRanks(ImplementationAssemblies resolver)
{
    private readonly Dictionary<(string AssemblyName, string Version, string? PackageId, string? Framework), int> _ranks = new();
    private readonly Dictionary<string, AssemblyFile?> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rank of a request's implementation assembly.</summary>
    /// <param name="request">The generation request; its member does not matter.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <exception cref="OperationCanceledException">The token or the enclosing work was cancelled.</exception>
    internal int Of(GenerationRequest request, CancellationToken cancellationToken)
    {
        WorkCancellation.Check();
        var key = (request.AssemblyName, request.Version, request.PackageId, request.Framework);
        if (_ranks.TryGetValue(key, out var rank))
            return rank;
        var resolution = resolver.Resolve(request.AssemblyName, request.Version, request.PackageId, request.Framework, cancellationToken);
        rank = resolution.Assembly is { } implementation && File(implementation.Path) is { } file ? Rank(implementation, file) : 0;
        _ranks.Add(key, rank);
        return rank;
    }

    /// <summary>The rank of an implementation in the graph of the files it is compiled against, each named by its assembly name.</summary>
    /// <param name="implementation">The implementation and its references.</param>
    /// <param name="file">The implementation's metadata.</param>
    private int Rank(ImplementationAssembly implementation, AssemblyFile file)
    {
        var files = new Dictionary<string, AssemblyFile>(StringComparer.OrdinalIgnoreCase) { [file.Name] = file };
        foreach (var path in implementation.References)
        {
            WorkCancellation.Check();
            if (File(path) is { } reference)
                files.TryAdd(reference.Name, reference);
        }

        return new Components(files).RankOf(file.Name);
    }

    /// <summary>A file's assembly metadata, read once per run of ranks.</summary>
    /// <param name="path">The file.</param>
    private AssemblyFile? File(string path)
    {
        if (!_files.TryGetValue(path, out var file))
            _files.Add(path, file = AssemblyFile.Read(path));
        return file;
    }

    /// <summary>The strongly connected components of one implementation's reference graph, found by Tarjan's walk from the implementation,
    /// each ranked as it closes: every component it references closed before it.</summary>
    /// <param name="files">The assemblies of the graph by name; a reference to a name outside it is no edge.</param>
    private sealed class Components(Dictionary<string, AssemblyFile> files)
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _low = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _rank = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<string> _open = new();
        private readonly HashSet<string> _onStack = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The rank of an assembly of the graph.</summary>
        /// <param name="name">Its name.</param>
        internal int RankOf(string name)
        {
            WorkCancellation.Check();
            Visit(name);
            return _rank[name];
        }

        /// <summary>One step of Tarjan's walk; closes the component of <paramref name="name"/> when it is the component's root.</summary>
        /// <param name="name">The assembly visited.</param>
        private void Visit(string name)
        {
            WorkCancellation.Check();
            _index[name] = _low[name] = _index.Count;
            _open.Push(name);
            _onStack.Add(name);
            foreach (var next in References(name))
            {
                WorkCancellation.Check();
                if (!_index.ContainsKey(next))
                {
                    Visit(next);
                    _low[name] = Math.Min(_low[name], _low[next]);
                }
                else if (_onStack.Contains(next))
                    _low[name] = Math.Min(_low[name], _index[next]);
            }
            if (_low[name] != _index[name])
                return;

            var component = new List<string>();
            string member;
            do
            {
                WorkCancellation.Check();
                member = _open.Pop();
                _onStack.Remove(member);
                component.Add(member);
            }
            while (!string.Equals(member, name, StringComparison.OrdinalIgnoreCase));
            var inside = component.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rank = component.SelectMany(References).Where(next => !inside.Contains(next)).Select(next => _rank[next] + 1).DefaultIfEmpty(0).Max();
            foreach (var each in component)
            {
                WorkCancellation.Check();
                _rank[each] = rank;
            }
        }

        /// <summary>The assemblies of the graph an assembly references, other than itself, by name in ordinal order.</summary>
        /// <param name="name">The assembly.</param>
        private IEnumerable<string> References(string name) =>
            files[name].References.Select(reference => reference.Name)
                       .Where(next => files.ContainsKey(next) && !string.Equals(next, name, StringComparison.OrdinalIgnoreCase))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Order(StringComparer.OrdinalIgnoreCase);
    }
}
