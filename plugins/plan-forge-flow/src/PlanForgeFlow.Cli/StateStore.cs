using System.Globalization;
using System.Text.Json.Nodes;

namespace PlanForgeFlow;

internal static class StateStore
{
    public static string StatePath(string workspace) => Path.Combine(workspace, ".forge", "state.json");

    public static ForgeState CreateEmpty() => ForgeStateSchema.CreateEmpty();

    public static ForgeState Load(string workspace)
    {
        var path = StatePath(workspace);
        if (!File.Exists(path)) throw new CliFailure("state", "no Forge state exists", 3);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state must not be a symlink", 3);
            for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null && parent.Exists; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new CliFailure("state", "Forge state path contains a symlinked directory", 3);
            }
            return ForgeState.FromJson(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
        }
        catch (CliFailure) { throw; }
        catch (Exception error) { throw new CliFailure("state", $"state is malformed: {error.Message}", 3); }
    }

    public static ForgeState Update(string workspace, Action<ForgeState> update) => Update(workspace, null, update);

    public static ForgeState Update(string workspace, ForgeState? expectedState, Action<ForgeState> update)
    {
        using var stateLock = ForgeStateLock.Acquire(workspace);
        var state = Load(workspace);
        if (expectedState is not null && !string.Equals(Hashing.Sha256Hex(state.ToJson().ToJsonString()), Hashing.Sha256Hex(expectedState.ToJson().ToJsonString()), StringComparison.Ordinal)) throw new CliFailure("state", "Forge state changed during the operation; retry against the current dispatch", 3);
        update(state);
        state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        DurableFiles.WriteJson(StatePath(workspace), state);
        return state;
    }
}
