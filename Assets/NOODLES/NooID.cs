using System;

using PeterO.Cbor;


/// <summary>
/// A NOODLES ID. This consists of a slot and a generation number
/// </summary>
public struct NooID : IEquatable<NooID>
{
    public uint slot;
    public uint gen;

    /// <summary>
    /// Parse an ID from CBOR
    /// </summary>
    /// <param name="value">Object to parse from. Should be a CBOR Array</param>
    /// <returns>New ID</returns>
    public static NooID FromCBOR(CBORObject value)
    {
        return new NooID
        {
            slot = value[0].ToObject<uint>(),
            gen = value[1].ToObject<uint>(),
        };
    }

    public readonly CBORObject ToCBOR()
    {
        return CBORObject.NewArray().Add(slot).Add(gen);
    }

    public override readonly bool Equals(object other)
    {
        if (other is not NooID) return false;
        NooID o = (NooID)other;
        return slot == o.slot && gen == o.gen;
    }

    public readonly bool Equals(NooID other)
    {
        return slot == other.slot && gen == other.gen;
    }

    public static bool operator ==(NooID a, NooID b)
    {
        return a.slot == b.slot && a.gen == b.gen;
    }

    public static bool operator !=(NooID a, NooID b)
    {
        return !(a.slot == b.slot && a.gen == b.gen);
    }

    public readonly bool IsNull()
    {
        const uint NULL = uint.MaxValue;
        return slot == NULL || gen == NULL;
    }

    public static NooID NULL_ID = new() { slot = uint.MaxValue, gen = uint.MaxValue};

    public override readonly int GetHashCode()
    {
        return HashCode.Combine(slot, gen);
    }
}
