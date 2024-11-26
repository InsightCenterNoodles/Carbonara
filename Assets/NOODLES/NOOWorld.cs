using PeterO.Cbor;


/// <summary>
/// Collection of all NOODLES components. We need this central location for
/// reporting contents to new clients.
/// </summary>
public class NOOWorld
{
    public ComponentList entity_list;
    public ComponentList geometry_list;
    public ComponentList material_list;
    public ComponentList texture_list;
    public ComponentList image_list;
    public ComponentList buffer_view_list;
    public ComponentList buffer_list;

    public NOOWorld(AsyncQueue<OutgoingMessage> n)
    {
        entity_list = new(n, new ComponentMessageIDs {
            create_mid = 4,
            update_mid = 5,
            delete_mid = 6,
        });

        geometry_list = new(n, new ComponentMessageIDs {
            create_mid = 26,
            update_mid = uint.MaxValue,
            delete_mid = 27,
        });

        material_list = new(n, new ComponentMessageIDs {
            create_mid = 14,
            update_mid = 15,
            delete_mid = 16,
        });

        texture_list = new(n, new ComponentMessageIDs
        {
            create_mid = 19,
            update_mid = uint.MaxValue,
            delete_mid = 20,
        });

        image_list = new(n, new ComponentMessageIDs
        {
            create_mid = 17,
            update_mid = uint.MaxValue,
            delete_mid = 18,
        });

        buffer_view_list = new(n, new ComponentMessageIDs {
            create_mid = 12,
            update_mid = uint.MaxValue,
            delete_mid = 13,
        });

        buffer_list = new(n, new ComponentMessageIDs {
            create_mid = 10,
            update_mid = uint.MaxValue,
            delete_mid = 11,
        });
    }


    /// <summary>
    /// Create a dump of all known components.
    /// </summary>
    /// <returns></returns>
    public CBORObject DumpToArray()
    {
        var arr = CBORObject.NewArray();

        buffer_list.DumpTo(arr);
        buffer_view_list.DumpTo(arr);
        image_list.DumpTo(arr);
        texture_list.DumpTo(arr);
        material_list.DumpTo(arr);
        geometry_list.DumpTo(arr);
        entity_list.DumpTo(arr);

        return arr;
    }
}