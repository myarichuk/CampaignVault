using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CampaignVault.Models;

namespace CampaignVault.Plugins;

/// <summary>
/// Open registry of WorldChange <c>$type</c> discriminators for JSON wire + commit schema.
/// Seeded from core <see cref="JsonDerivedTypeAttribute"/> values; plugin assemblies add
/// <see cref="PluginWorldChangeAttribute"/> types after load. Handler dispatch is separate
/// (see WorldChangeDispatcher.FindHandler fallback) and must not be rewritten here.
/// </summary>
public sealed class WorldChangeTypeRegistry
{
    public static WorldChangeTypeRegistry Instance { get; } = CreateDefault();

    private readonly object _gate = new();
    private readonly Dictionary<string, Type> _byDiscriminator = new(StringComparer.Ordinal);
    private int _version;

    public int Version
    {
        get { lock (_gate) return _version; }
    }

    public IReadOnlyDictionary<string, Type> Entries
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyDictionary<string, Type>(new Dictionary<string, Type>(_byDiscriminator, StringComparer.Ordinal));
            }
        }
    }

    public static WorldChangeTypeRegistry CreateDefault()
    {
        var registry = new WorldChangeTypeRegistry();
        registry.SeedFromJsonDerivedTypes();
        return registry;
    }

    public void SeedFromJsonDerivedTypes()
    {
        foreach (var attr in typeof(WorldChange).GetCustomAttributes<JsonDerivedTypeAttribute>())
        {
            var discriminator = attr.TypeDiscriminator as string
                ?? throw new InvalidOperationException(
                    $"JsonDerivedType on WorldChange for {attr.DerivedType.Name} lacks a string discriminator.");
            Register(discriminator, attr.DerivedType);
        }
    }

    public void RegisterPluginAssemblies(IEnumerable<Assembly> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type is null || type.IsAbstract || !typeof(WorldChange).IsAssignableFrom(type))
                    continue;

                var attr = type.GetCustomAttribute<PluginWorldChangeAttribute>();
                if (attr is null)
                    continue;

                Register(attr.Discriminator, type);
            }
        }
    }

    public void Register(string discriminator, Type changeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        ArgumentNullException.ThrowIfNull(changeType);

        if (!typeof(WorldChange).IsAssignableFrom(changeType) || changeType.IsAbstract)
        {
            throw new ArgumentException(
                $"Type {changeType.FullName} must be a non-abstract WorldChange subtype.",
                nameof(changeType));
        }

        lock (_gate)
        {
            if (_byDiscriminator.TryGetValue(discriminator, out var existing) && existing != changeType)
            {
                throw new InvalidOperationException(
                    $"WorldChange $type '{discriminator}' collision: already registered as {existing.FullName}, cannot register {changeType.FullName}.");
            }

            _byDiscriminator[discriminator] = changeType;
            _version++;
        }
    }

    public bool TryGet(string discriminator, out Type? type)
    {
        lock (_gate)
        {
            return _byDiscriminator.TryGetValue(discriminator, out type);
        }
    }

    /// <summary>Removes a discriminator. Test-only: production registrations are additive for process lifetime.</summary>
    public bool Unregister(string discriminator)
    {
        lock (_gate)
        {
            if (!_byDiscriminator.Remove(discriminator))
                return false;

            _version++;
            return true;
        }
    }

    /// <summary>STJ resolver that opens WorldChange polymorphism from this registry.</summary>
    public IJsonTypeInfoResolver CreateTypeInfoResolver() =>
        new DefaultJsonTypeInfoResolver
        {
            Modifiers = { ApplyWorldChangePolymorphism }
        };

    private void ApplyWorldChangePolymorphism(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(WorldChange))
            return;

        var options = new JsonPolymorphismOptions
        {
            TypeDiscriminatorPropertyName = "$type",
            IgnoreUnrecognizedTypeDiscriminators = false,
        };

        lock (_gate)
        {
            foreach (var (discriminator, clrType) in _byDiscriminator)
            {
                options.DerivedTypes.Add(new JsonDerivedType(clrType, discriminator));
            }
        }

        typeInfo.PolymorphismOptions = options;
    }
}
