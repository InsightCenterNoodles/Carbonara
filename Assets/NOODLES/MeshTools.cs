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

    readonly int _index_index;

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
    CBORObject _patch_part;

    RegisteredTexture _base_color_texture;

    public NOOMeshConverter(Mesh m, Material mat, int index)
    {
        Debug.Log($"Starting conversion {m.GetInstanceID()} {mat.GetInstanceID()} {index}");
        // This is slow right now. We read from the CPU side of things and convert. No memcpys available here :(
        // Note that meshes need to have some kind of readable flag set so we can do this.
        // We can speed this up by directly stealing from the GPU buffer, which is likely in a better format
        _mesh = m;
        _material = mat;
        _index_index = index;

        _positions = m.vertices;
        _normals = m.normals;
        _uv = m.uv;

        _index_list = m.GetIndices(_index_index);

        // Assuming some byte sizes of Vec3 and Vec2
        _position_byte_size = (3 * 4);
        _normal_byte_size = (3 * 4) * (_normals.Length > 0 ? 1 : 0);
        _uv_byte_size = (2 * 4) * (_uv.Length > 0 ? 1 : 0);

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
    bool AddAttribute(Vector2[] attrib_data)
    {
        if (attrib_data.Length < _mesh.vertexCount)
        {
            return false;
        }

        for (int v_i = 0; v_i < _mesh.vertexCount; v_i++)
        {
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].x), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].y), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
        }

        return true;
    }

    /// <summary>
    /// Append a 3d vector attribute to the byte buffer
    /// </summary>
    /// <param name="attrib_data"></param>
    bool AddAttribute(Vector3[] attrib_data)
    {
        if (attrib_data.Length < _mesh.vertexCount)
        {
            return false;
        }

        for (int v_i = 0; v_i < _mesh.vertexCount; v_i++)
        {
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].x), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].y), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
            System.Buffer.BlockCopy(BitConverter.GetBytes(attrib_data[v_i].z), 0, _mesh_data, _cursor, sizeof(float));
            _cursor += sizeof(float);
        }

        return true;
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

    public struct MaterialData
    {
        public Color baseColor;
        public Texture baseColorTexture;
        public float roughness;
        public float metallic;
        public bool transparency;
        public float opacity;
    }

    public static MaterialData ExtractMaterialData(Material material)
    {
        MaterialData data = new MaterialData();
        if (material == null)
        {
            Debug.LogWarning("Material is null!");
            return data;
        }
        // Handle Standard Shader
        if (material.shader.name == "Standard")
        {
            Debug.Log("Standard material");
            data.baseColor = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            data.baseColorTexture = material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
            data.metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
            data.roughness = material.HasProperty("_Glossiness") ? 1f - material.GetFloat("_Glossiness") : 1f; // Inverted glossiness for roughness
            // Transparency (if rendering mode is transparent)
            data.transparency = material.HasProperty("_Mode") && Mathf.Approximately(material.GetFloat("_Mode"), 3f);
            data.opacity = data.transparency ? data.baseColor.a : 1.0f;
        }
        // Handle Universal Render Pipeline (URP) shaders
        else if (material.shader.name.Contains("Universal Render Pipeline"))
        {
            Debug.Log($"URP material : {material.shader.name} {material.GetFloat("_Surface")}");
            // Lit variant
            if (material.shader.name.Contains("Lit"))
            {
                data.baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
                data.baseColorTexture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
                data.metallic = material.HasProperty("_Metallic") ? material.GetFloat("_Metallic") : 0f;
                data.roughness = material.HasProperty("_Smoothness") ? 1f - material.GetFloat("_Smoothness") : 1f; // Smoothness to roughness
                // Transparency
                data.transparency = material.HasProperty("_Surface") && material.GetFloat("_Surface") == 1f; // Surface type: Transparent
                data.opacity = data.transparency ? data.baseColor.a : 1f;
            }
            // Unlit variant
            else if (material.shader.name.Contains("Unlit"))
            {
                data.baseColor = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor") : Color.white;
                data.baseColorTexture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
                // Unlit shaders typically have no metallic, roughness, or transparency
                data.metallic = 0f;
                data.roughness = 1f;
                data.transparency = false;
                data.opacity = 1f;
            }
        }
        else
        {
            Debug.LogWarning($"Unsupported shader: {material.shader.name}");
        }
        return data;
    }

    

    /// <summary>
    /// Convert a material to NOODLES format
    /// </summary>
    void MakeMat()
    {
        var extract = ExtractMaterialData(_material);

        // Create PBR info block
        var pbr_info = CBORObject.NewMap()
            .Add("name", _material.name)
            .Add("base_color", ColorToArray(extract.baseColor))
            .Add("metallic", extract.metallic)
            .Add("roughness", extract.roughness)
            .Add("use_alpha", extract.transparency)
            ;

        if (extract.baseColorTexture != null)
        {
            _base_color_texture = NOORegistries.Instance.TextureRegistry.CheckRegister(
                extract.baseColorTexture,
                () => { return new RegisteredTexture(extract.baseColorTexture); }
            );

            var tex_ref = CBORObject.NewMap().Add("texture", _base_color_texture.NoodlesID());

            pbr_info.Add("base_color_texture", tex_ref);
        }

        Debug.Log($"New mat {pbr_info}");

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
        Debug.Log("START CONVERT " + _cursor + " " + _mesh.vertexCount);
        AddAttribute(_positions);
        Debug.Log("AFTER POS " + _cursor);
        bool normal_ok = AddAttribute(_normals);
        Debug.Log("AFTER NORMAL " + _cursor);
        bool uv_ok = AddAttribute(_uv);
        Debug.Log("AFTER UV " + _cursor);

        AddIndex(_index_list);

        Debug.Log("AFTER INDEX " + _cursor);

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
        if (normal_ok) AddAttributeInfo(3 * 4, "NORMAL", "VEC3");
        if (uv_ok) AddAttributeInfo(2 * 4, "TEXTURE", "VEC2");

        MakeMat();

        var index_part = CBORObject.NewMap()
            .Add("view", _buffer_view.IDAsCBOR())
            .Add("offset", _vertex_buffer_size)
            .Add("count", _index_list.Length)
            .Add("format", "U32");

        _patch_part = CBORObject.NewMap()
            .Add("attributes", _attributes)
            .Add("vertex_count", _mesh.vertexCount)
            .Add("indices", index_part)
            .Add("type", "TRIANGLES")
            .Add("material", _mat_component.IDAsCBOR())
            ;
        Debug.Log("END CONVERT " + _cursor + " " + _mesh.vertexCount);
    }

    public RegisteredBuffer Buffer() { return _buffer;  }
    public NOOComponent BufferView() { return _buffer_view; }
    public NOOComponent MaterialComponent() { return _mat_component; }
    public CBORObject PatchPart() { return _patch_part; }
    public RegisteredTexture BaseColorTexture() { return _base_color_texture; }
}
