using System.Collections.Generic;

using UnityEngine;
using PeterO.Cbor;

/// <summary>
/// A CBOR object that represents a NOODLES component. Can be updated.
/// </summary>
public class NOOComponent
{
    //private NooID _identity;
    readonly CBORObject _identity_cbor;
    readonly CBORObject _data;
    readonly ComponentMessageSink _sink;

    /// <summary>
    /// Create a new component
    /// </summary>
    /// <param name="id">Identity of this component</param>
    /// <param name="sink">A queue to send create, update, and destroy messages</param>
    /// <param name="data">Initial component content</param>
    public NOOComponent(NooID id, ComponentMessageSink sink, CBORObject data)
    {
        //_identity = id;
        _identity_cbor = id.ToCBOR();
        _data = data;
        _sink = sink;

        // Now here we have a problem. If we send data as is to the output
        // queue, the item could be being processed in one thread
        // while being modified in another.

        // So, for the moment, we make a copy

        var copy = CBORObject.NewMap();

        foreach (var k_v in _data.Entries)
        {
            copy[k_v.Key] = k_v.Value;
        }
        
        _sink.PublishCreate(copy);
    }

    /// <returns>The identity of this component as a CBOR object</returns>
    public CBORObject IDAsCBOR()
    {
        return _identity_cbor;
    }

    /// <summary>
    /// Update this component with new content. Keys in the given object will either add or overwrite existing keys in the component
    /// </summary>
    /// <param name="delta">A delta object</param>
    public void PatchSet(CBORObject delta)
    {
        delta["id"] = _identity_cbor;

        _sink.PublishUpdate(delta);

        foreach (var k_v in delta.Entries)
        {
            _data[k_v.Key] = k_v.Value;
        }
    }

    /// <summary>
    /// Obtain the content of this component
    /// </summary>
    /// <returns>The component content</returns>
    public CBORObject Data()
    {
        return _data;
    }

    ~NOOComponent()
    {
        var m = CBORObject.NewMap();
        m["id"] = _identity_cbor;
        _sink.PublishDelete(m);
    }
}

/// <summary>
/// Message IDs to be used for creation, update, and deletion NOODLES messages
/// </summary>
public struct ComponentMessageIDs
{
    public uint create_mid;
    public uint update_mid;
    public uint delete_mid;
}

/// <summary>
/// A queue to send messages. This links message IDs to the main broadcast queue.
/// </summary>
public class ComponentMessageSink
{
    readonly AsyncQueue<OutgoingMessage> _notify;
    ComponentMessageIDs _ids;

    /// <summary>
    /// Create a new sink
    /// </summary>
    /// <param name="n">Global broadcast queue</param>
    /// <param name="ids">NOODLES message IDS to use</param>
    public ComponentMessageSink(AsyncQueue<OutgoingMessage> n, ComponentMessageIDs ids)
    {
        _notify = n;
        _ids = ids;
    }

    /// <summary>
    /// Send a component creation message
    /// </summary>
    /// <param name="obj">Content to send</param>
    public void PublishCreate(CBORObject obj)
    {
        var array = CBORObject.NewArray().Add(_ids.create_mid).Add(obj);
        Debug.Log($"Create {array}");
        _notify.Enqueue(new OutgoingMessage(array));
    }

    /// <summary>
    /// Send a component update message
    /// </summary>
    /// <param name="obj">Content delta to send</param>
    public void PublishUpdate(CBORObject obj)
    {
        var array = CBORObject.NewArray().Add(_ids.update_mid).Add(obj);
        //Debug.Log($"Update {array}");
        _notify.Enqueue(new OutgoingMessage(array));
    }

    /// <summary>
    /// Send a component delete message
    /// </summary>
    public void PublishDelete(CBORObject obj)
    {
        var array = CBORObject.NewArray().Add(_ids.delete_mid).Add(obj);
        Debug.Log($"Delete {array}");
        _notify.Enqueue(new OutgoingMessage(array));
    }

    public ComponentMessageIDs MessageIDs()
    {
        return _ids;
    }
}

/// <summary>
/// A list of NOODLES components
/// </summary>
public class ComponentList
{
    /// <summary>
    /// Active components
    /// </summary>
    readonly Dictionary<NooID, NOOComponent> _active = new();

    /// <summary>
    /// Counter for new component IDs
    /// </summary>
    uint _highwater = 0;

    /// <summary>
    /// List of free IDs that can be reused
    /// </summary>
    readonly List<NooID> _used = new();

    /// <summary>
    /// Message collection queue. Given to each component
    /// </summary>
    readonly ComponentMessageSink _sink;

    /// <summary>
    /// Create a new component list
    /// </summary>
    /// <param name="notify">Broadcast queue</param>
    /// <param name="ids">Component message IDs</param>
    public ComponentList(AsyncQueue<OutgoingMessage> notify, ComponentMessageIDs ids)
    {
        _sink = new ComponentMessageSink(notify, ids);
    }

    /// <summary>
    /// Generate a new ID from an unused slot
    /// </summary>
    /// <returns>A new ID</returns>
    private NooID GenerateNewID()
    {
        var new_id = new NooID
        {
            slot = _highwater,
            gen = 0
        };

        _highwater += 1;

        return new_id;
    }

    /// <summary>
    /// Attempt to obtain or generate a new ID
    /// </summary>
    /// <returns>A new component ID</returns>
    private NooID ProvisionID()
    {
        // If we have no IDs to reuse, allocate new slot
        if (_used.Count == 0)
        {
            return GenerateNewID();
        }

        // Take an ID to re-use
        var last = _used[^1];

        _used.RemoveAt(_used.Count - 1);

        // But, if this slot is exhausted, allocate new slot
        if (last.gen == uint.MaxValue - 1)
        {
            return GenerateNewID();
        }

        last.gen += 1;

        return last;
    }

    /// <summary>
    /// Allocate a new component and broadcast to clients.
    /// </summary>
    /// <param name="content">Initial component content. ID will be added</param>
    /// <returns>A strong handle to a NOODLES components</returns>
    public NOOComponent Register(CBORObject content)
    {
        var id = ProvisionID();

        content["id"] = id.ToCBOR();

        var comp = new NOOComponent(id, _sink, content);

        _active[id] = comp;

        return comp;
    }

    /// <summary>
    /// Helper function to build a complete list of active components to be sent to new clients
    /// </summary>
    /// <param name="arr">Destination array to write to</param>
    public void DumpTo(CBORObject arr)
    {
        var create_id = CBORObject.FromObject(_sink.MessageIDs().create_mid);

        foreach (var k_v in _active) {
            arr.Add(create_id);
            arr.Add(k_v.Value.Data());
        }
        
    }

    // Debugging only!
    public List<CBORObject> SplitDump()
    {
        var create_id = CBORObject.FromObject(_sink.MessageIDs().create_mid);

        List<CBORObject> ret = new();

        foreach (var k_v in _active)
        {
            CBORObject arr = CBORObject.NewArray();
            arr.Add(create_id);
            arr.Add(k_v.Value.Data());
            ret.Add(arr);
        }

        return ret;
    }
}
