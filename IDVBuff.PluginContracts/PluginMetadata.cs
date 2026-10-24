using System.Reflection;
using System.Runtime.CompilerServices;

namespace IDVBuff.PluginContracts;

/// <summary>
/// Immutable attribute values shared by the host and its views. Repeated
/// attribute construction also recreates reflection invocation metadata after
/// short-lived member caches disappear. Weak type keys allow collectible plugin
/// assemblies to unload; no plugin instance, page or settings object is retained.
/// </summary>
public sealed class PluginMetadata
{
    private static readonly ConditionalWeakTable<Type, Lazy<PluginMetadata?>> Types = new();

    private PluginMetadata(PluginAttribute attribute)
    {
        Id = attribute.Id;
        DisplayName = attribute.DisplayName;
        Description = attribute.Description;
        Version = attribute.Version;
        Author = attribute.Author;
        EnabledByDefault = attribute.EnabledByDefault;
        AlwaysActive = attribute.AlwaysActive;
    }

    public string Id { get; }
    public string? DisplayName { get; }
    public string? Description { get; }
    public string? Version { get; }
    public string? Author { get; }
    public bool EnabledByDefault { get; }
    public bool AlwaysActive { get; }

    public static PluginMetadata? For(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        return Types.GetValue(pluginType, static type => new Lazy<PluginMetadata?>(() =>
            type.GetCustomAttribute<PluginAttribute>(inherit: false) is { } attribute
                ? new PluginMetadata(attribute) : null)).Value;
    }
}
