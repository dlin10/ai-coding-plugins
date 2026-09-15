namespace ConcurrencyHunter.Ir;

public static class IrValidator
{
    public static IReadOnlyList<string> Validate(IrBody body)
    {
        var problems = new List<string>();
        if (!string.Equals(body.SchemaVersion, IrSchema.VERSION, StringComparison.Ordinal))
            problems.Add($"Schema version '{body.SchemaVersion}' is not supported; expected '{IrSchema.VERSION}'.");

        var values = new HashSet<int>();
        foreach (var value in body.Values)
        {
            if (!values.Add(value.Id))
                problems.Add($"Value %{value.Id} is declared twice.");
        }

        ValidateBlocks(body, values, problems);
        ValidateRegions(body, problems);
        return problems;
    }

    private static void ValidateBlocks(IrBody body, IReadOnlySet<int> values, List<string> problems)
    {
        var blockOrdinals = body.Blocks.Select(block => block.Ordinal).ToHashSet();
        for (var index = 0; index < body.Blocks.Count; index++)
        {
            if (body.Blocks[index].Ordinal != index)
            {
                problems.Add("Block ordinals are not contiguous from zero.");
                break;
            }
        }

        if (body.Blocks.Count == 0 || body.Blocks[0].Kind != IrBlockKind.Entry)
            problems.Add("The first block is not Entry.");
        if (body.Blocks.Count == 0 || body.Blocks[^1].Kind != IrBlockKind.Exit)
            problems.Add("The last block is not Exit.");

        var definedValues = new HashSet<int>();
        for (var blockIndex = 0; blockIndex < body.Blocks.Count; blockIndex++)
        {
            var block = body.Blocks[blockIndex];
            if (blockIndex > 0 && block.Kind == IrBlockKind.Entry)
                problems.Add($"Entry block b{block.Ordinal} is misplaced.");
            if (blockIndex < body.Blocks.Count - 1 && block.Kind == IrBlockKind.Exit)
                problems.Add($"Exit block b{block.Ordinal} is misplaced.");

            ValidateFlowPredecessors(block, problems);
            ValidateOperations(block, values, definedValues, problems);
            ValidateBranch(block.ConditionalBranch, blockOrdinals, values, problems);
            ValidateBranch(block.FallThroughBranch, blockOrdinals, values, problems);
        }
    }

    private static void ValidateFlowPredecessors(IrBlock block, List<string> problems)
    {
        for (var index = 1; index < block.FlowPredecessors.Count; index++)
        {
            var previous = block.FlowPredecessors[index - 1];
            var current = block.FlowPredecessors[index];
            if (previous.BlockOrdinal > current.BlockOrdinal ||
                previous.BlockOrdinal == current.BlockOrdinal && previous.EdgeKind >= current.EdgeKind)
            {
                problems.Add($"Flow predecessors of b{block.Ordinal} are not distinct and ordered.");
                break;
            }
        }
    }

    private static void ValidateOperations(IrBlock block, IReadOnlySet<int> values,
                                           HashSet<int> definedValues, List<string> problems)
    {
        var sawNonPhi = false;
        foreach (var operation in block.Operations)
        {
            if (operation is IrPhiOperation phi)
            {
                if (sawNonPhi)
                    problems.Add($"Phi operation {phi.Id} is not at the start of b{block.Ordinal}.");
                ValidatePhi(block, phi, problems);
            }
            else
            {
                sawNonPhi = true;
            }

            foreach (var definedValue in operation.DefinedValues)
            {
                if (!definedValues.Add(definedValue))
                    problems.Add($"Value %{definedValue} is defined twice.");
            }

            foreach (var operand in operation.Operands)
            {
                if (!values.Contains(operand))
                    problems.Add($"Operation {operation.Id} names unknown operand %{operand}.");
            }
        }
    }

    private static void ValidatePhi(IrBlock block, IrPhiOperation phi, List<string> problems)
    {
        if (phi.Inputs.Count != block.FlowPredecessors.Count)
        {
            problems.Add($"Phi operation {phi.Id} has {phi.Inputs.Count} inputs for " +
                         $"{block.FlowPredecessors.Count} flow predecessors.");
            return;
        }

        for (var index = 0; index < phi.Inputs.Count; index++)
        {
            if (phi.Inputs[index].Predecessor != block.FlowPredecessors[index])
            {
                problems.Add($"Phi operation {phi.Id} inputs do not match flow predecessor order.");
                break;
            }
        }
    }

    private static void ValidateBranch(IrBranch? branch, IReadOnlySet<int> blockOrdinals,
                                       IReadOnlySet<int> values, List<string> problems)
    {
        if (branch is null)
            return;

        if (branch.Destination is int destination && !blockOrdinals.Contains(destination))
            problems.Add($"{branch.Kind} branch targets block b{destination}, which is outside the body.");
        if (branch.ConditionValue is int condition && !values.Contains(condition))
            problems.Add($"{branch.Kind} branch names unknown operand %{condition}.");
    }

    private static void ValidateRegions(IrBody body, List<string> problems)
    {
        var regions = body.Regions.ToDictionary(region => region.Id);
        foreach (var region in body.Regions)
        {
            if (region.Parent is not int parentId)
                continue;

            if (!regions.TryGetValue(parentId, out var parent))
            {
                problems.Add($"Region r{region.Id} names missing parent r{parentId}.");
                continue;
            }

            if (region.FirstBlockOrdinal < parent.FirstBlockOrdinal ||
                region.LastBlockOrdinal > parent.LastBlockOrdinal)
            {
                problems.Add($"Region r{region.Id} is outside parent r{parentId}.");
            }
        }
    }
}
