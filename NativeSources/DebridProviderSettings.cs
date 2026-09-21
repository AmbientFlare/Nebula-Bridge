using NebulaBridge.Config;

namespace NebulaBridge.NativeSources;

/// <summary>Runtime view of one provider's configuration. The credential is never logged or serialized.</summary>
public sealed record DebridProviderSettings(
    string ProviderId,
    bool Enabled,
    int Priority,
    string Credential
)
{
    public bool Configured => Enabled && !string.IsNullOrWhiteSpace(Credential);
}

/// <summary>
/// Single seam between provider implementations and the plugin configuration. Implementations
/// look up their own settings by stable provider id; nothing else needs to know where a
/// credential comes from.
/// </summary>
public interface IDebridProviderSettingsProvider
{
    DebridProviderSettings GetSettings(string providerId);
}

/// <summary>
/// Maps stable provider ids to their environment variables and write-only configuration
/// fields. This is the only place that pairs a provider id with a secret field; the admin
/// secret API, plugin secret updates, and provider implementations all go through it.
/// </summary>
public static class DebridProviderCredentials
{
    private sealed record Slot(
        string ProviderId,
        string[] EnvironmentVariables,
        Func<PluginConfiguration, string> Read,
        Action<PluginConfiguration, string> Write
    );

    private static readonly Slot[] Slots =
    [
        new(
            "torbox",
            ["NEBULA_BRIDGE_TORBOX_API_TOKEN"],
            cfg => cfg.TorBoxApiToken,
            (cfg, value) => cfg.TorBoxApiToken = value
        ),
        new(
            "realdebrid",
            ["NEBULA_BRIDGE_REALDEBRID_API_TOKEN"],
            cfg => cfg.RealDebridApiToken,
            (cfg, value) => cfg.RealDebridApiToken = value
        ),
    ];

    public static IReadOnlyList<string> ProviderIds { get; } = Slots.Select(slot => slot.ProviderId).ToArray();

    public static bool IsKnown(string? providerId) => Find(providerId) is not null;

    /// <summary>True when an environment variable supplies the credential (the saved value is ignored).</summary>
    public static bool IsEnvironmentManaged(string? providerId) =>
        Find(providerId)?.EnvironmentVariables.Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) == true;

    public static string Resolve(PluginConfiguration? configuration, string? providerId)
    {
        var slot = Find(providerId);
        if (slot is null)
            return string.Empty;
        foreach (var name in slot.EnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return configuration is null ? string.Empty : slot.Read(configuration)?.Trim() ?? string.Empty;
    }

    public static bool HasCredential(PluginConfiguration? configuration, string? providerId) =>
        !string.IsNullOrWhiteSpace(Resolve(configuration, providerId));

    public static bool TryWrite(PluginConfiguration configuration, string? providerId, string value)
    {
        var slot = Find(providerId);
        if (slot is null)
            return false;
        slot.Write(configuration, value);
        return true;
    }

    /// <summary>Copies every provider secret from one configuration object to another.</summary>
    public static void CopySecrets(PluginConfiguration source, PluginConfiguration target)
    {
        foreach (var slot in Slots)
            slot.Write(target, slot.Read(source));
    }

    private static Slot? Find(string? providerId)
    {
        var id = DebridProviderConfig.NormalizeId(providerId);
        return Slots.FirstOrDefault(slot => slot.ProviderId == id);
    }
}

public sealed class PluginDebridProviderSettingsProvider : IDebridProviderSettingsProvider
{
    public DebridProviderSettings GetSettings(string providerId)
    {
        var id = DebridProviderConfig.NormalizeId(providerId);
        var configuration = NebulaBridgePlugin.Instance?.Configuration;
        var entry = configuration?.GetDebridProvider(id);
        return new DebridProviderSettings(
            id,
            entry?.Enabled == true,
            entry?.Priority ?? DebridProviderConfig.MaxPriority,
            DebridProviderCredentials.Resolve(configuration, id)
        );
    }
}
