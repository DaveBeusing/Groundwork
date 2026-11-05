using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Groundwork.Core;

namespace Groundwork.Ecs;

/// <summary>Builds a frozen catalog of local, copy-out component encodings.</summary>
public sealed class ComponentRegistryBuilder
{
    private readonly Dictionary<Type, ComponentId> _types = [];
    private readonly Dictionary<ComponentTypeId, int> _sizes = [];

    public ComponentRegistryBuilder Register<T>(ComponentTypeId id) where T : unmanaged
    {
        if (id.Value == 0 || _sizes.ContainsKey(id) || _types.ContainsKey(typeof(T)))
            throw new ArgumentException("A component requires a unique nonzero ID and a unique type.", nameof(id));
        if (ContainsAddress(typeof(T)))
            throw new ArgumentException("A component cannot contain pointers or native handles.", nameof(T));

        int size = Unsafe.SizeOf<T>();
        _types.Add(typeof(T), new ComponentId(id, size));
        _sizes.Add(id, size);
        return this;
    }

    public ComponentRegistry Build() => new(_types.ToImmutableDictionary(), _sizes.ToImmutableDictionary());

    private static bool ContainsAddress(Type type)
    {
        if (type.IsPointer || type.IsFunctionPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
            return true;
        if (type.IsPrimitive || type.IsEnum)
            return false;
        return type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => ContainsAddress(field.FieldType));
    }
}

internal readonly record struct ComponentId(ComponentTypeId Value, int Size);

public sealed class ComponentRegistry
{
    private readonly ImmutableDictionary<Type, ComponentId> _types;
    private readonly ImmutableDictionary<ComponentTypeId, int> _sizes;

    internal ComponentRegistry(ImmutableDictionary<Type, ComponentId> types, ImmutableDictionary<ComponentTypeId, int> sizes)
        => (_types, _sizes) = (types, sizes);

    public int Count => _sizes.Count;

    public ComponentTypeId IdOf<T>() where T : unmanaged
        => _types.TryGetValue(typeof(T), out var component) ? component.Value :
            throw new InvalidOperationException("The component type is not registered.");

    public ImmutableArray<byte> Encode<T>(T value) where T : unmanaged
    {
        if (!_types.TryGetValue(typeof(T), out var component))
            throw new InvalidOperationException("The component type is not registered.");
        var buffer = new byte[component.Size];
        MemoryMarshal.Write(buffer, in value);
        return ImmutableCollectionsMarshal.AsImmutableArray(buffer);
    }

    internal bool TryGetId<T>(out ComponentId id) where T : unmanaged => _types.TryGetValue(typeof(T), out id);
    internal bool TryGetSize(ComponentTypeId id, out int size) => _sizes.TryGetValue(id, out size);
    internal static T Decode<T>(ImmutableArray<byte> bytes) where T : unmanaged => MemoryMarshal.Read<T>(bytes.AsSpan());
}
