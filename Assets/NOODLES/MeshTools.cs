using System;
using UnityEngine;
using System.Collections.Generic;

using PeterO.Cbor;

/// <summary>
/// Tool to convert meshes from Unity to NOODLES formats
/// </summary>
public class NOOMeshConverter
{
    /// <summary>
    /// Current mesh we are operating on
    /// </summary>
    readonly Mesh _mesh;

    /// <summary>
    /// NOODLES meshes need to know about materials
    /// </summary>
    readonly Material _material;

    /// <summary>
    /// Vertex positions
    /// </summary>
    readonly Vector3[] _positions;

    /// <summary>
    /// Vertex normals
    /// </summary>
    readonly Vector3[] _normals;

    /// <summary>
    /// Vertex UV coordinates
    /// </summary>
    readonly Vector2[] _uv;

    /// <summary>
    /// Triangle indexing
    /// </summary>
    readonly int[] _index_list;

    /// <summary>
    /// The size of a position attribute
    /// </summary>
    readonly int _position_byte_size = 0;

    /// <summary>
    /// The size of a normal attribute
    /// </summary>
    readonly int _normal_byte_size = 0;

    /// <summary>
    /// The size of the texture attribute
    /// </summary>
    readonly int _uv_byte_size = 0;

    /// <summary>
    /// The byte size of a vertex
    /// </summary>
    readonly int _vertex_size = 0;

    /// <summary>
    /// The total number of bytes required to store vertex information
    /// </summary>
    readonly int _vertex_buffer_size = 0;

    /// <summary>
    /// The total number of bytes required to store index information
    /// </summary>
    readonly int _index_buffer_size = 0;

    /// <summary>
    /// Byte storage destination for mesh info
    /// </summary>
    readonly byte[] _mesh_data;

    /// <summary>
    /// Write cursor into _mesh_data
    /// </summary>
    int _cursor = 0;

    /// <summary>
    /// List of attributes in NOODLES format
    /// </summary>
    readonly List<CBORObject> _attributes = new();

    /// <summary>
    /// NOODLES buffer component
    /// </summary>
    RegisteredBuffer _buffer;
    NOOComponent _buffer_view;
    NOOComponent _mat_component;
    NOOComponent _mesh_component;

    public NOOMeshConverter(Mesh m, Material mat)
    {
        // This is slow right now. We read from the CPU side of things and convert. No memcpys available here :(
        // Note that meshes need to have some kind of readable flag set so we can do this.
        // We can speed this up by directly stealing from the GPU buffer, which is likely in a better format
        _mesh = m;
        _material = mat;

        _positions = m.vertices;
        _normals = m.normals;
        _uv = m.uv;

        _index_list = m.GetIndices(0);

        // Assuming some byte sizes of Vec3 and Vec2
        _position_byte_size = (3 * 4);
        _normal_byte_size = (3 * 4);
        _uv_byte_size = (2 * 4);

        _vertex_size = _position_byte_size + _normal_byte_size + _uv_byte_size;
        _vertex_buffer_size = _vertex_size * m.vertexCount;

        _index_buffer_size = _index_list.Length * 4;

        _mesh_data = new byte[_vertex_buffer_size + _index_buffer_size];

        Debug.Log($"Setup geom");
        Debug.Log($"Sizes {_position_byte_size} {_normal_byte_size} {_uv_byte_size}");
        Debug.Log($"Vertex size {_vertex_size} {_vertex_buffer_size} {_index_buffer_size}");
        Debug.Log($"Buffer size {_mesh_data.Length}");

        // Shift handedness...
        Translate();

        // Build byte buffer
        BuildBytes();
    }

    /// <summary>
    /// Append a 2d vector attribute to the byte buffer
    /// </summary>
    /// <param name="attrib_data"></param>
    void AddAttribute(Vector2[] attrib_data)
    {
        for (int v_i = 0; v_i < _mesh.vertexCount; v_i++)
        {
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].x), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].y), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
        }
    }

    /// <summary>
    /// Append a 3d vector attribute to the byte buffer
    /// </summary>
    /// <param name="attrib_data"></param>
    void AddAttribute(Vector3[] attrib_data)
    {
        for (int v_i = 0; v_i < _mesh.vertexCount; v_i++)
        {
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].x), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].y), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].z), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
        }
    }

    /// <summary>
    /// Append the index information to the byte buffer
    /// </summary>
    /// <param name="index_info"></param>
    void AddIndex(int[] index_info)
    {
        var byte_count = index_info.Length * 4;
        System.Buffer.BlockCopy(index_info, 0, _mesh_data, _cursor, byte_count);
        _cursor += byte_count;
    }

    /// <summary>
    /// Add a NOODLES attribute to the list
    /// </summary>
    /// <param name="format_size"></param>
    /// <param name="semantic"></param>
    /// <param name="format"></param>
    void AddAttributeInfo(int format_size, string semantic, string format)
    {
        var obj = CBORObject.NewMap()
            .Add("view", _buffer_view.IDAsCBOR())
            .Add("semantic", semantic)
            .Add("format", format)
            .Add("offset", _cursor)
            .Add("stride", format_size)
            ;

        _attributes.Add(obj);

        _cursor += format_size * _mesh.vertexCount;
    }

    /// <summary>
    /// Convert a color to a NOODLES array
    /// </summary>
    /// <param name="c"></param>
    /// <returns></returns>
    static float[] ColorToArray(Color c)
    {
        var color = new float[4];
        color[0] = c.r;
        color[1] = c.g;
        color[2] = c.b;
        color[3] = c.a;
        return color;
    }

    /// <summary>
    /// Convert a material to NOODLES format
    /// </summary>
    void MakeMat()
    {
        var color = _material.color;

        // Set do defaults
        var metallic = 1.0;
        var roughness = 1.0;

        // The default shaders have this property for metallic.
        // TODO: Check support for other shaders
        if (_material.HasProperty("_Metallic"))
        {
            Debug.Log("Has metallic");
            metallic = _material.GetFloat("_Metallic");
        }

        // Now we need to figure out roughness. Some shaders have it defined
        // in the inverse way
        if (_material.HasProperty("_Smoothness"))
        {
            float smoothness = _material.GetFloat("_Smoothness");
            roughness = 1.0f - smoothness;
        }
        else if (_material.HasProperty("_Roughness"))
        {
            Debug.Log("Has roughness");
            roughness = _material.GetFloat("_Roughness");
        }
        else if (_material.HasProperty("_Glossiness"))
        {
            Debug.Log("Has glossiness");
            float glossiness = _material.GetFloat("_Glossiness");
            roughness = 1.0f - glossiness;
        }
        
        // Store albedo color
        if (_material.HasProperty("_BaseColor"))
        {
            color = _material.GetColor("_BaseColor");
        }
        else if (_material.HasProperty("_Color"))
        {
            color = _material.GetColor("_Color");
        }

        if (_material.HasProperty("_MainTex"))
        {
            Texture color_texture = _material.GetTexture("_MainTex");

            if (color_texture != null)
            {

            }

            //(color_texture != null ? color_texture.name : "None")
            //Debug.Log("Main Texture: " + );
        }

        Debug.Log($"M {color} {metallic} {roughness}");

        // Create PBR info block
        var pbr_info = CBORObject.NewMap()
            .Add("name", _material.name)
            .Add("base_color", ColorToArray(color))
            .Add("metallic", metallic)
            .Add("roughness", roughness)
            ;

        // Complete NOODLES material
        _mat_component = NOOServer.Instance.World().material_list.Register(
            CBORObject.NewMap()
            .Add("pbr_info", pbr_info)
        );
    }

    /// <summary>
    /// Translate to the NOODLES specified coordinate system. This is a shift from left to right handed
    /// </summary>
    void Translate()
    {
        // Swap z coord for positions and normals
        for (int i = 0; i < _positions.Length; i++)
        {
            _positions[i].z = - _positions[i].z;
            _normals[i].z = -_normals[i].z;
        }

        // Fix winding to ensure CCW
        for (int i = 0; i < _index_list.Length; i += 3)
        {
            int temp = _index_list[i];
            _index_list[i] = _index_list[i + 2];
            _index_list[i + 2] = temp;
        }
    }

    /// <summary>
    /// Write mesh data to bytes
    /// </summary>
    void BuildBytes()
    {

        AddAttribute(_positions);
        AddAttribute(_normals);
        AddAttribute(_uv);

        AddIndex(_index_list);

        // Register the buffer
        _buffer = new RegisteredBuffer(_mesh_data);

        // Register the view
        _buffer_view = NOOServer.Instance.World().buffer_view_list.Register(
            CBORObject.NewMap()
            .Add("source_buffer", _buffer.NoodlesID())
            .Add("type", "GEOMETRY")
            .Add("offset", 0)
            .Add("length", _mesh_data.Length)
        );

        // Now register the geometry. We care about a single patch for now

        _cursor = 0;

        AddAttributeInfo(3 * 4, "POSITION", "VEC3");
        AddAttributeInfo(3 * 4, "NORMAL", "VEC3");
        AddAttributeInfo(2 * 4, "TEXTURE", "VEC2");

        MakeMat();

        var index_part = CBORObject.NewMap()
            .Add("view", _buffer_view.IDAsCBOR())
            .Add("offset", _vertex_buffer_size)
            .Add("count", _index_list.Length)
            .Add("format", "U32");

        var patch = CBORObject.NewMap()
            .Add("attributes", _attributes)
            .Add("vertex_count", _mesh.vertexCount)
            .Add("indices", index_part)
            .Add("type", "TRIANGLES")
            .Add("material", _mat_component.IDAsCBOR())
            ;

        _mesh_component = NOOServer.Instance.World().geometry_list.Register(
            CBORObject.NewMap().Add("name", _mesh.name).Add("patches", CBORObject.NewArray().Add(patch))
        );
    }

    public RegisteredBuffer Buffer() { return _buffer;  }
    public NOOComponent BufferView() { return _buffer_view; }
    public NOOComponent MaterialComponent() { return _mat_component; }
    public NOOComponent MeshComponent() { return _mesh_component; }
}
