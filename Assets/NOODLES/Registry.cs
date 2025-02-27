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
    List<RegisteredBuffer> _buffer = new();

    /// <summary>
    /// Buffer view this mesh uses
    /// </summary>
    List<NOOComponent> _buffer_view = new();

    List<RegisteredTexture> _reg_textures = new();

    /// <summary>
    /// Material this mesh uses
    /// </summary>
    List<NOOComponent> _mat_component = new();

    /// <summary>
    /// Mesh component that mirrors this mesh
    /// </summary>
    NOOComponent _mesh_component;

    public RegisteredMeshMat(Mesh mesh, Material[] mats)
    {
        Debug.Log("Building new mesh and material");
        var patch_list = CBORObject.NewArray();

        for (int i = 0; i < mats.Length; i++)
        {
            var convert = new NOOMeshConverter(mesh, mats[i], i);

            _buffer.Add(convert.Buffer());
            _buffer_view.Add(convert.BufferView());
            _mat_component.Add(convert.MaterialComponent());

            _reg_textures.Add(convert.BaseColorTexture());

            patch_list.Add(convert.PatchPart());
        }

        var content = CBORObject.NewMap().Add("name", mesh.name).Add("patches", patch_list);


        _mesh_component = NOOServer.Instance.World().geometry_list.Register(content);
    }

    public CBORObject NoodlesID()
    {
        return _mesh_component.IDAsCBOR();
    }
}

/// <summary>
/// Represents a texture exported over NOODLES. Automatically cleans itself up after going out of scope.
/// </summary>
public class RegisteredTexture
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

    static byte[] MakeTextureBytes(Texture texture)
    {
        if (texture == null)
        {
            return null;
        }

        if (!texture.isReadable)
        {
            Debug.LogError("Texture is not readable; ensure the texture is readable in the import settings");
            return null;
        }

        if (texture is Texture2D tex2d)
        {
            var bytes = tex2d.EncodeToPNG();

            if (bytes == null)
            {
                Debug.LogError("Unable to encode texture to bytes");
            }

            return bytes;
        }

        Debug.LogError("Unable to handle non-2d textures at this time");
        return null;
    }

    public RegisteredTexture(Texture texture)
    {
        var tex_bytes = MakeTextureBytes(texture);

        if (tex_bytes == null)
        {
            return;
        }

        _buffer = new RegisteredBuffer(tex_bytes);

        // Register the view
        _buffer_view = NOOServer.Instance.World().buffer_view_list.Register(
            CBORObject.NewMap()
            .Add("source_buffer", _buffer.NoodlesID())
            .Add("type", "GEOMETRY")
            .Add("offset", 0)
            .Add("length", tex_bytes.Length)
        );

        // Register the image
        _image = NOOServer.Instance.World().image_list.Register(
            CBORObject.NewMap()
            .Add("buffer_source", _buffer_view.IDAsCBOR())
        );

        // Register the texture
        _texture = NOOServer.Instance.World().texture_list.Register(
            CBORObject.NewMap()
            .Add("name", texture.name)
            .Add("image", _image.IDAsCBOR())
        );

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
    readonly Dictionary<UnityItem, WeakReference<NOOItem>> _registry;


    private int _install_counter = 0;

    /// <summary>
    /// Create a registry
    /// </summary>
    public NOORegistry()
    {
        _registry = new();
    }

    /// <summary>
    /// Create a registry
    /// </summary>
    public NOORegistry(IEqualityComparer<UnityItem> comparer) {
        _registry = new(comparer);
    }

    /// <summary>
    /// Check if a noodles mirror exists for a unity item (resource). If not, it creates one.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public NOOItem CheckRegister(UnityItem item, Func<NOOItem> func)
    {
        Debug.Log($"Checking for existing {item.GetHashCode()}");
        if (!_registry.TryGetValue(item, out var value))
        {
            Debug.Log($"Need to build {item.GetHashCode()}");
            value = InstallItem(item, func);
        }

        // Value should be valid now

        if (value.TryGetTarget(out NOOItem rm))
        {
            Debug.Log("Target exists, returning");
            return rm;
        }

        // No target. Which means we need to rebuild

        value = InstallItem(item, func);

        value.TryGetTarget(out rm);

        return rm;
    }

    /// <summary>
    /// Add an item to this cache. 
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    private WeakReference<NOOItem> InstallItem(UnityItem item, Func<NOOItem> func)
    {
        _install_counter++;

        if (_install_counter > 10)
        {
            CleanUpStaleEntries();
        }

        var ret = new WeakReference<NOOItem>(func());
        _registry[item] = ret;
        return ret;
    }

    /// <summary>
    /// Clear out stale, or already discarded entries from the dictionary.
    /// </summary>
    /// After a while, we will have a number of already collected entries; this
    /// function will clear those out, and should be called periodically. We do this in the install.
    /// TODO REMOVE
    private void CleanUpStaleEntries()
    {
        Debug.Log("Cleaning old references");
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

class LRUCache<Key, Value>
{
    class Slot
    {
        public Key key;
        public Value value;
    };

    readonly List<Slot> _linear;
    readonly Dictionary<Key, Slot> _storage;
    private readonly Func<Key, Value> _maker;

    public Value Get(Key key) {
        if (_storage.TryGetValue(key, out var slot))
        {
            return slot.value;
        }

        // need to insert
        var new_value = _maker(key);

        while (_linear.Count > 64)
        {
            var at = _linear.Count - 1;
            _storage.Remove(_linear[at].key);
            _linear.RemoveAt(at);
        }

        var new_slot = new Slot {
            key = key,
            value = new_value,
        };

        _storage[key] = new_slot;
        _linear.Prepend(new_slot);

        return new_value;
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
    public NOORegistry<ValueTuple<Mesh, Material[]>, RegisteredMeshMat> MeshRegistry = new(new MeshMatComparer());

    public NOORegistry<Texture, RegisteredTexture> TextureRegistry = new();

    NOORegistries()
    {

    }
}

class MeshMatComparer : IEqualityComparer<ValueTuple<Mesh, Material[]>> {
    public bool Equals((Mesh, Material[]) x, (Mesh, Material[]) y)
    {
        return GetHashCode(x) == GetHashCode(y);
    }

    public int GetHashCode((Mesh, Material[]) obj)
    {
        int arr_hash = 0;
        foreach (var mat in obj.Item2)
        {
            arr_hash = HashCode.Combine(mat, arr_hash);
        }
        return obj.Item1.GetHashCode() ^ arr_hash;
    }
}
