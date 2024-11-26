using System;
using System.Collections.Generic;
using UnityEngine;
using PeterO.Cbor;
using System.Linq;

/// <summary>
/// A block of bytes that has been published to NOODLES. Automatically cleans up when goes out of scope
/// </summary>
public class RegisteredBuffer
{
    /// <summary>
    /// Component that describes this buffer
    /// </summary>
    readonly NOOComponent _buffer;

    /// <summary>
    /// An optional HTTP published handle to the block of bytes
    /// </summary>
    readonly Asset _asset;

    /// <summary>
    /// Create a new registered buffer. Inlines data if less than 1kb, otherwise publishes over http
    /// </summary>
    /// <param name="bytes">raw data</param>
    public RegisteredBuffer(byte[] bytes)
    {

        var content = CBORObject.NewMap().Add("size", bytes.Length);

        if (bytes.Length > 1024)
        {
            // ship as asset
            _asset = new Asset(bytes);
            content.Add("uri_bytes", CBORObject.NewMap()
                .Add("scheme", "http")
                .Add("path", $"{_asset.Identity}")
                .Add("port", $"{_asset.Port}"));
        }
        else
        {
            content.Add("inline_bytes", bytes);
        }


        _buffer = NOOServer.Instance.World().buffer_list.Register(content);
    }

    public CBORObject NoodlesID()
    {
        return _buffer.IDAsCBOR();
    }
}


/// <summary>
/// Represents a mesh exported over NOODLES. Automatically cleans itself up after going out of scope.
/// </summary>
class RegisteredMeshMat
{
    /// <summary>
    /// Buffer this registered mesh uses
    /// </summary>
    RegisteredBuffer _buffer;

    /// <summary>
    /// Buffer view this mesh uses
    /// </summary>
    NOOComponent _buffer_view;

    /// <summary>
    /// Material this mesh uses
    /// </summary>
    NOOComponent _mat_component;

    /// <summary>
    /// Mesh component that mirrors this mesh
    /// </summary>
    NOOComponent _mesh_component;

    public RegisteredMeshMat(ValueTuple<Mesh, Material> pack)
    {
        var convert = new NOOMeshConverter(pack.Item1, pack.Item2);

        _buffer = convert.Buffer();
        _buffer_view = convert.BufferView();
        _mat_component = convert.MaterialComponent();
        _mesh_component = convert.MeshComponent();
    }

    public CBORObject NoodlesID()
    {
        return _mesh_component.IDAsCBOR();
    }
}

/// <summary>
/// Represents a texture exported over NOODLES. Automatically cleans itself up after going out of scope.
/// </summary>
class RegisteredTexture
{
    /// <summary>
    /// Buffer this registered texture uses
    /// </summary>
    RegisteredBuffer _buffer;

    /// <summary>
    /// Buffer view this texture uses
    /// </summary>
    NOOComponent _buffer_view;
    NOOComponent _image;
    NOOComponent _texture;


    public RegisteredTexture(Texture texture)
    {
        
    }

    public CBORObject NoodlesID()
    {
        return _texture.IDAsCBOR();
    }
}

/// <summary>
/// An abstract registry of convered Unity items
/// </summary>
/// <typeparam name="UnityItem">The Unity-side type we register</typeparam>
/// <typeparam name="NOOItem">The corresponding NOODLES side</typeparam>
class NOORegistry<UnityItem, NOOItem> where NOOItem : class
{
    // Map of unity items to noodles items
    readonly Dictionary<UnityItem, WeakReference<NOOItem>> _registry = new();

    // because C# is weak with generics...
    private Func<UnityItem, NOOItem> _maker;

    private int _install_counter = 0;

    /// <summary>
    /// Create a registry
    /// </summary>
    /// <param name="f">A function that takes a unity item and returns a mirror noodles item</param>
    public NOORegistry(Func<UnityItem, NOOItem> f)
    {
        _maker = f;
    }

    /// <summary>
    /// Check if a noodles mirror exists for a unity item (resource). If not, it creates one.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public NOOItem CheckRegister(UnityItem item)
    {
        if (!_registry.TryGetValue(item, out var value))
        {
            value = InstallItem(item);
        }

        if (value.TryGetTarget(out NOOItem rm))
        {
            return rm;
        }

        value = InstallItem(item);

        value.TryGetTarget(out rm);

        return rm;
    }

    /// <summary>
    /// Add an item to this cache. 
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    private WeakReference<NOOItem> InstallItem(UnityItem item)
    {
        _install_counter++;

        if (_install_counter > 10)
        {
            CleanUpStaleEntries();
        }

        var ret = new WeakReference<NOOItem>(_maker(item));
        _registry[item] = ret;
        return ret;
    }

    /// <summary>
    /// Clear out stale, or already discarded entries from the dictionary.
    /// </summary>
    /// After a while, we will have a number of already collected entries; this
    /// function will clear those out, and should be called periodically. We do this in the install.
    private void CleanUpStaleEntries()
    {
        _install_counter = 0;

        // Identify keys to remove where the reference is no longer valid
        var staleKeys = _registry
            .Where(kv => !kv.Value.TryGetTarget(out var _)) // Try to get target; if it fails, the entry is stale
            .Select(kv => kv.Key)
            .ToList();

        // Remove stale entries
        foreach (var key in staleKeys)
        {
            _registry.Remove(key);
        }
    }
}

/// <summary>
/// Global list of registries
/// </summary>
class NOORegistries
{
    public static NOORegistries Instance = new();

    /// <summary>
    /// Registry for meshes and materials
    /// </summary>
    public NOORegistry<ValueTuple<Mesh, Material>, RegisteredMeshMat> MeshRegistry = new((ValueTuple<Mesh, Material> m) => new RegisteredMeshMat(m));

    //public NOORegistry<Texture, RegisteredTexture> TextureRegistry = new()

    NOORegistries()
    {

    }
}