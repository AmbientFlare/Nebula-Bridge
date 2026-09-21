using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace NebulaBridge.Services;

/// <summary>
/// Reads an existing authorization from Jellyfin's official Trakt plugin without taking a
/// compile-time dependency on that optional plugin. Tokens never leave the server process.
/// </summary>
public sealed class JellyfinTraktAccountBridge(ILogger<JellyfinTraktAccountBridge> logger)
{
    private const string TraktAssemblyName = "Trakt";
    private const string TraktPluginTypeName = "Trakt.Plugin";
    private const string TraktUrisTypeName = "Trakt.Api.TraktUris";
    private const string RedirectUri = "urn:ietf:wg:oauth:2.0:oob";

    public TraktSettings? GetSettings(bool enabled)
    {
        try
        {
            var context = FindContext();
            if (context is null)
            {
                return null;
            }

            var user = FindAuthorizedUser(context.Value.Configuration);
            if (user is null)
            {
                return null;
            }

            if (NormalizeNullableArray(user, "LocationsExcluded"))
            {
                context.Value.Plugin.GetType().GetMethod("SaveConfiguration", Type.EmptyTypes)
                    ?.Invoke(context.Value.Plugin, null);
                logger.LogInformation(
                    "Normalized Jellyfin Trakt's optional location list for compatibility."
                );
            }

            var clientId = GetConstant(context.Value.UrisType, "ClientId");
            var clientSecret = GetConstant(context.Value.UrisType, "ClientSecret");
            var accessToken = GetString(user, "AccessToken");
            if (
                string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret)
                || string.IsNullOrWhiteSpace(accessToken)
            )
            {
                return null;
            }

            var expiration = GetDateTime(user, "AccessTokenExpiration");
            var (createdAt, expiresIn) = ToTokenLifetime(expiration);
            return new TraktSettings(
                enabled,
                clientId,
                clientSecret,
                RedirectUri,
                accessToken,
                GetString(user, "RefreshToken"),
                createdAt,
                expiresIn,
                string.Empty,
                "jellyfin",
                GetGuid(user, "LinkedMbUserId")?.ToString("D")
            );
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or TargetInvocationException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Could not inspect Jellyfin's optional Trakt plugin.");
            return null;
        }
    }

    public bool TrySaveTokens(string? linkedUserId, TraktTokenResponse token)
    {
        if (!Guid.TryParse(linkedUserId, out var expectedUserId))
        {
            return false;
        }

        try
        {
            var context = FindContext();
            if (context is null)
            {
                return false;
            }

            var user = GetUsers(context.Value.Configuration)
                .FirstOrDefault(candidate => GetGuid(candidate, "LinkedMbUserId") == expectedUserId);
            if (user is null)
            {
                return false;
            }

            SetProperty(user, "AccessToken", token.AccessToken);
            SetProperty(user, "RefreshToken", token.RefreshToken);
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(token.CreatedAt)
                .AddSeconds(token.ExpiresIn)
                .LocalDateTime;
            SetProperty(user, "AccessTokenExpiration", expiresAt);
            context.Value.Plugin.GetType().GetMethod("SaveConfiguration", Type.EmptyTypes)?.Invoke(
                context.Value.Plugin,
                null
            );
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not update the inherited Jellyfin Trakt authorization.");
            return false;
        }
    }

    private static TraktPluginContext? FindContext()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.GetName().Name,
                    TraktAssemblyName,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        var pluginType = assembly?.GetType(TraktPluginTypeName, throwOnError: false);
        var urisType = assembly?.GetType(TraktUrisTypeName, throwOnError: false);
        var plugin = pluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);
        var configuration = plugin?.GetType().GetProperty("PluginConfiguration")?.GetValue(plugin)
            ?? plugin?.GetType().GetProperty("Configuration")?.GetValue(plugin);
        return plugin is not null && configuration is not null && urisType is not null
            ? new TraktPluginContext(plugin, configuration, urisType)
            : null;
    }

    private static object? FindAuthorizedUser(object configuration) =>
        GetUsers(configuration)
            .Where(user => !string.IsNullOrWhiteSpace(GetString(user, "AccessToken")))
            .OrderBy(user => GetGuid(user, "LinkedMbUserId") ?? Guid.Empty)
            .FirstOrDefault();

    private static IEnumerable<object> GetUsers(object configuration) =>
        configuration.GetType().GetProperty("TraktUsers")?.GetValue(configuration) is IEnumerable users
            ? users.Cast<object>()
            : [];

    private static string GetConstant(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()
            as string
        ?? string.Empty;

    private static string GetString(object instance, string name) =>
        instance.GetType().GetProperty(name)?.GetValue(instance) as string ?? string.Empty;

    private static Guid? GetGuid(object instance, string name) =>
        instance.GetType().GetProperty(name)?.GetValue(instance) is Guid value ? value : null;

    private static DateTime? GetDateTime(object instance, string name) =>
        instance.GetType().GetProperty(name)?.GetValue(instance) is DateTime value ? value : null;

    private static void SetProperty(object instance, string name, object value)
    {
        var property = instance.GetType().GetProperty(name);
        if (property?.CanWrite != true)
        {
            throw new InvalidOperationException($"Jellyfin Trakt property {name} is unavailable.");
        }

        property.SetValue(instance, value);
    }

    internal static bool NormalizeNullableArray(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        if (
            property?.CanWrite != true
            || !property.PropertyType.IsArray
            || property.GetValue(instance) is not null
        )
        {
            return false;
        }

        var elementType = property.PropertyType.GetElementType();
        if (elementType is null)
        {
            return false;
        }

        property.SetValue(instance, Array.CreateInstance(elementType, 0));
        return true;
    }

    private static (long CreatedAt, int ExpiresIn) ToTokenLifetime(DateTime? expiration)
    {
        var now = DateTimeOffset.UtcNow;
        if (expiration is null || expiration == DateTime.MinValue)
        {
            return (now.AddMinutes(-10).ToUnixTimeSeconds(), 1);
        }

        var expiresAt = expiration.Value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(expiration.Value, TimeSpan.Zero)
            : new DateTimeOffset(expiration.Value.ToUniversalTime(), TimeSpan.Zero);
        var remaining = Math.Clamp((long)(expiresAt - now).TotalSeconds, 1, int.MaxValue);
        return (now.ToUnixTimeSeconds(), (int)remaining);
    }

    private readonly record struct TraktPluginContext(
        object Plugin,
        object Configuration,
        Type UrisType
    );
}
