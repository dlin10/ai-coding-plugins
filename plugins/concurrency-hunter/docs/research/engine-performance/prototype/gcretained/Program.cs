// Retained sizes from a .gcdump: the dominator tree of the object graph (Cooper-Harvey-Kennedy over reverse postorder from the
// dump's root), then per type the retained size of its outermost instances (an instance dominated by another of its own type is
// already inside that one), and the largest single objects with the chain of types that dominate them.
//   gcretained <file.gcdump> [top] [type-name-substring] [exact type name whose dominator-tree children to list]
using Graphs;

var top = args.Length > 1 ? int.Parse(args[1]) : 15;
var dump = new GCHeapDump(args[0]);
var graph = dump.MemoryGraph;
var limit = (int)graph.NodeIndexLimit;
Console.WriteLine($"{args[0]}: {graph.NodeCount:N0} nodes, {graph.TotalSize / 1048576.0:N1} MB, {graph.TotalNumberOfReferences:N0} references; " +
                  $"process working set {dump.TotalProcessWorkingSet / 1048576.0:N0} MB, commit {dump.TotalProcessCommit / 1048576.0:N0} MB; " +
                  $"average count multiplier {dump.AverageCountMultiplier}, size multiplier {dump.AverageSizeMultiplier}");

// The graph as arrays: sizes, types, children (CSR).
var size = new int[limit];
var type = new int[limit];
var childStart = new int[limit + 1];
var children = new List<int>(graph.TotalNumberOfReferences);
var node = graph.AllocNodeStorage();
for (var i = 0; i < limit; i++)
{
    childStart[i] = children.Count;
    graph.GetNode((NodeIndex)i, node);
    size[i] = node.Size;
    type[i] = (int)node.TypeIndex;
    for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
        children.Add((int)child);
}

childStart[limit] = children.Count;
var edges = children.ToArray();
children = null;

// Reverse postorder from the root.
var root = (int)graph.RootIndex;
var rpoOf = new int[limit];
Array.Fill(rpoOf, -1);
var postorder = new List<int>();
var visited = new bool[limit];
var stack = new Stack<(int Node, int Next)>();
stack.Push((root, childStart[root]));
visited[root] = true;
while (stack.Count != 0)
{
    var (current, next) = stack.Pop();
    if (next < childStart[current + 1])
    {
        stack.Push((current, next + 1));
        var child = edges[next];
        if (!visited[child])
        {
            visited[child] = true;
            stack.Push((child, childStart[child]));
        }
    }
    else
    {
        postorder.Add(current);
    }
}

var reachable = postorder.Count;
var order = new int[reachable]; // rpo index -> node
for (var k = 0; k < reachable; k++)
{
    order[k] = postorder[reachable - 1 - k];
    rpoOf[order[k]] = k;
}

postorder = null;

// Predecessors in rpo index space (CSR).
var predCount = new int[reachable + 1];
for (var k = 0; k < reachable; k++)
{
    var n = order[k];
    for (var e = childStart[n]; e < childStart[n + 1]; e++)
        predCount[rpoOf[edges[e]] + 1]++;
}

for (var k = 0; k < reachable; k++)
    predCount[k + 1] += predCount[k];
var preds = new int[predCount[reachable]];
var fill = (int[])predCount.Clone();
for (var k = 0; k < reachable; k++)
{
    var n = order[k];
    for (var e = childStart[n]; e < childStart[n + 1]; e++)
        preds[fill[rpoOf[edges[e]]]++] = k;
}

fill = null;

// Immediate dominators (rpo index space).
var idom = new int[reachable];
Array.Fill(idom, -1);
idom[0] = 0;
var changed = true;
var passes = 0;
while (changed)
{
    changed = false;
    passes++;
    for (var b = 1; b < reachable; b++)
    {
        var chosen = -1;
        for (var p = predCount[b]; p < predCount[b + 1]; p++)
        {
            var pred = preds[p];
            if (idom[pred] == -1)
                continue;
            if (chosen == -1)
            {
                chosen = pred;
                continue;
            }

            var (x, y) = (pred, chosen);
            while (x != y)
            {
                while (x > y)
                    x = idom[x];
                while (y > x)
                    y = idom[y];
            }

            chosen = x;
        }

        if (chosen != -1 && idom[b] != chosen)
        {
            idom[b] = chosen;
            changed = true;
        }
    }
}

// Retained sizes: a node's idom precedes it in rpo order.
var retained = new long[reachable];
for (var k = 0; k < reachable; k++)
    retained[k] = size[order[k]];
for (var k = reachable - 1; k > 0; k--)
    retained[idom[k]] += retained[k];

// Per type: count, shallow, and the retained size of its outermost instances (a dominator-tree walk counting types on the path).
var typeLimit = (int)graph.NodeTypeIndexLimit;
var count = new long[typeLimit];
var shallow = new long[typeLimit];
var retainedByType = new long[typeLimit];
var onPath = new int[typeLimit];
var domChildStart = new int[reachable + 1];
for (var k = 1; k < reachable; k++)
    domChildStart[idom[k] + 1]++;
for (var k = 0; k < reachable; k++)
    domChildStart[k + 1] += domChildStart[k];
var domChildren = new int[Math.Max(reachable - 1, 0)];
var domFill = (int[])domChildStart.Clone();
for (var k = 1; k < reachable; k++)
    domChildren[domFill[idom[k]]++] = k;
domFill = null;

var walk = new Stack<(int Node, bool Exit)>();
walk.Push((0, false));
while (walk.Count != 0)
{
    var (k, exit) = walk.Pop();
    var t = type[order[k]];
    if (exit)
    {
        onPath[t]--;
        continue;
    }

    count[t]++;
    shallow[t] += size[order[k]];
    if (onPath[t] == 0)
        retainedByType[t] += retained[k];
    onPath[t]++;
    walk.Push((k, true));
    for (var c = domChildStart[k]; c < domChildStart[k + 1]; c++)
        walk.Push((domChildren[c], false));
}

var typeNode = graph.AllocTypeNodeStorage();
string TypeName(int t) => graph.GetType((NodeTypeIndex)t, typeNode).Name;
string Mb(long bytes) => $"{bytes / 1048576.0,9:N1} MB";

Console.WriteLine($"reachable {reachable:N0} of {limit:N0} nodes; dominators in {passes} passes; root retains {Mb(retained[0])}");
Console.WriteLine($"\ntop {top} types by retained size (outermost instances)        count      shallow     retained");
foreach (var t in Enumerable.Range(0, typeLimit).OrderByDescending(t => retainedByType[t]).Take(top))
    Console.WriteLine($"  {Truncate(TypeName(t), 100),-100} {count[t],12:N0} {Mb(shallow[t])} {Mb(retainedByType[t])}");
Console.WriteLine($"\ntop {top} types by shallow size                                    count      shallow     retained");
foreach (var t in Enumerable.Range(0, typeLimit).OrderByDescending(t => shallow[t]).Take(top))
    Console.WriteLine($"  {Truncate(TypeName(t), 100),-100} {count[t],12:N0} {Mb(shallow[t])} {Mb(retainedByType[t])}");

if (args.Length > 2)
{
    Console.WriteLine($"\ntypes matching \"{args[2]}\"                                  count      shallow     retained");
    foreach (var t in Enumerable.Range(0, typeLimit).Where(t => count[t] != 0 && TypeName(t).Contains(args[2], StringComparison.Ordinal))
                                .OrderByDescending(t => shallow[t]))
        Console.WriteLine($"  {Truncate(TypeName(t), 140),-140} {count[t],12:N0} {Mb(shallow[t])} {Mb(retainedByType[t])}");
}

if (args.Length > 3)
{
    // The dominator-tree children of the largest object of an exactly named type: for an object holding its own collections, its
    // fields' objects, with their reference counts (an array's are its non-null slots).
    var owner = Enumerable.Range(0, reachable).Where(k => TypeName(type[order[k]]) == args[3]).OrderByDescending(k => retained[k]).FirstOrDefault(-1);
    Console.WriteLine($"\ndominator-tree children of the largest {args[3]} ({(owner < 0 ? "none" : Mb(retained[owner]))})");
    if (owner >= 0)
    {
        foreach (var c in Enumerable.Range(domChildStart[owner], domChildStart[owner + 1] - domChildStart[owner]).Select(c => domChildren[c])
                                    .OrderByDescending(c => retained[c]))
        {
            var n = order[c];
            var grandchildren = domChildStart[c + 1] - domChildStart[c];
            Console.WriteLine($"  {Mb(retained[c])} {Mb(size[n])}  refs {childStart[n + 1] - childStart[n],10:N0}  {Truncate(TypeName(type[n]), 130)}");
            if (grandchildren != 0)
                foreach (var g in Enumerable.Range(domChildStart[c], grandchildren).Select(g => domChildren[g]).OrderByDescending(g => retained[g]).Take(3))
                    Console.WriteLine($"      {Mb(retained[g])} {Mb(size[order[g]])}  refs {childStart[order[g] + 1] - childStart[order[g]],10:N0}  {Truncate(TypeName(type[order[g]]), 120)}");
        }
    }
}

Console.WriteLine($"\ntop {top} objects by retained size, with the types dominating them (nearest first)");
foreach (var k in Enumerable.Range(1, reachable - 1).OrderByDescending(k => retained[k]).Take(top))
{
    var chain = new List<string>();
    for (var d = idom[k]; d != 0 && chain.Count < 6; d = idom[d])
        chain.Add(Truncate(TypeName(type[order[d]]), 70));
    Console.WriteLine($"  {Mb(retained[k])}  {Truncate(TypeName(type[order[k]]), 90)}  (children {childStart[order[k] + 1] - childStart[order[k]]:N0})");
    Console.WriteLine($"      <- {string.Join(" <- ", chain)}");
}

static string Truncate(string text, int width) => text.Length <= width ? text : text[..(width - 3)] + "...";
