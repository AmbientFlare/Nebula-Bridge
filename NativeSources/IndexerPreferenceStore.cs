namespace NebulaBridge.NativeSources;

public interface IIndexerPreferenceStore
{
    IReadOnlyCollection<string> GetEnabledIds();

    bool SetEnabled(string id, bool enabled);
}

public sealed class PluginIndexerPreferenceStore : IIndexerPreferenceStore
{
    public IReadOnlyCollection<string> GetEnabledIds() =>
        NebulaBridgePlugin.Instance?.Configuration.EnabledNativeIndexerIds ?? [];

    public bool SetEnabled(string id, bool enabled)
    {
        var plugin = NebulaBridgePlugin.Instance;
        if (plugin is null)
        {
            return false;
        }

        var ids = new HashSet<string>(
            plugin.Configuration.EnabledNativeIndexerIds,
            StringComparer.OrdinalIgnoreCase
        );
        if (enabled)
        {
            ids.Add(id);
        }
        else
        {
            ids.Remove(id);
        }

        plugin.Configuration.EnabledNativeIndexerIds = ids
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        plugin.SaveConfiguration();
        return true;
    }
}
