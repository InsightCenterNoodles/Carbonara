using UnityEngine;

using System;
using System.Collections.Generic;
using System.Linq;

using PeterO.Cbor;

/// <summary>
///  Watches a GameObject and replicates it over NOODLES
/// </summary>
public class NOOWatcher : MonoBehaviour
{
    /// <summary>
    /// NOODLES Entity component that mirrors this object
    /// </summary>
    NOOComponent _component;

    /// <summary>
    /// The last parent this gameobject has seen
    /// </summary>
    Transform _last_parent = null;

    /// <summary>
    /// Last transform this gameobject has seen
    /// </summary>
    Vector3 _last_position;
    Quaternion _last_rotation;
    Vector3 _last_scale;

    /// <summary>
    /// Last mesh and material this object has seen
    /// </summary>
    Mesh _last_mesh = null; 
    Material[] _last_mats = null;

    /// <summary>
    /// Strong handle to a converted Unity mesh that mirrors the `_last_mesh`.
    /// </summary>
    RegisteredMeshMat _last_registered_mesh;

    private void Start()
    {
        var init = CBORObject.NewMap()
            .Add("transform", RightHandTransform())
            .Add("name", name);
        
        _component = NOOServer.Instance.World().entity_list.Register(init);

        OnTransformChildrenChanged();
    }

    private void Update()
    {
        var delta = CBORObject.NewMap();

        // Check if we need to actually send a delta

        bool need_patch = false;

        need_patch |= CheckUpdateTransform(delta);
        need_patch |= CheckUpdateParent(delta);
        need_patch |= CheckUpdateMesh(delta);

        if (need_patch)
        {
            _component.PatchSet(delta);
        }
        
    }

    /// <summary>
    /// Check if the transform has changed
    /// </summary>
    /// <param name="delta">Object to write changes to</param>
    /// <returns>true if a change has occurred</returns>
    private bool CheckUpdateTransform(CBORObject delta)
    {
        bool delta_p = transform.localPosition != _last_position;
        bool delta_r = transform.localRotation != _last_rotation;
        bool delta_s = transform.localScale != _last_scale;

        if (delta_p || delta_r || delta_s)
        {

            _last_position = transform.localPosition;
            _last_rotation = transform.localRotation;
            _last_scale = transform.localScale;

            delta.Add("transform", RightHandTransform());

            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if the parent has changed
    /// </summary>
    /// <param name="delta">Object to write changes to</param>
    /// <returns>true if a change has occurred</returns>
    private bool CheckUpdateParent(CBORObject delta)
    {
        if (transform.parent == _last_parent)
        {
            return false;
        }

        _last_parent = transform.parent;

        var parent_id = NooID.NULL_ID.ToCBOR();

        if (_last_parent != null) {
            var parent_watcher = _last_parent.GetComponent<NOOWatcher>();

            if (parent_watcher != null)
            {
                parent_id = parent_watcher._component.IDAsCBOR();
            }
        }

        delta.Add("parent", parent_id);

        return true;
    }

    /// <summary>
    /// Helper function for mesh and material delta detection
    /// </summary>
    /// <param name="delta">Object to write changes to</param>
    /// <param name="new_mesh">Current mesh from the filter</param>
    /// <param name="new_material">Current material from the renderer</param>
    /// <returns>true if changes have occurred</returns>
    private bool CheckUpdateGeometryParts(CBORObject delta, Mesh new_mesh, Material[] new_materials)
    {
        if (new_mesh == _last_mesh)
        {
            if (_last_mats != null && new_materials != null)
            {
                if (Enumerable.SequenceEqual(new_materials, _last_mats))
                {
                    return false;
                }
            } else
            {
                if (_last_mats == new_materials)
                {
                    return false;
                }
            }
        }

        _last_mesh = new_mesh;
        _last_mats = new_materials;

        if (_last_mesh != null && _last_mats != null)
        {
            Debug.Log("Adding new mesh rep");
            _last_registered_mesh = NOORegistries.Instance.MeshRegistry.CheckRegister(
                    new ValueTuple<Mesh, Material[]>(_last_mesh, new_materials)
                );

            var render_rep = CBORObject.NewMap().Add("mesh", _last_registered_mesh.NoodlesID());

            delta.Add("render_rep", render_rep);
        }
        else
        {
            Debug.Log("Clearing mesh rep");
            _last_registered_mesh = null;
            delta.Add("null_rep", CBORObject.True);
        }

        return true;
        
    }

    /// <summary>
    /// Check if the geometry or material has changed
    /// </summary>
    /// <param name="delta">Object to write changes to</param>
    /// <returns>true if a change has occurred</returns>
    private bool CheckUpdateMesh(CBORObject delta)
    {
        var filter = GetComponent<MeshFilter>();
        var renderer = GetComponent<Renderer>();

        Mesh mesh = (filter != null && filter.sharedMesh != null) ? filter.sharedMesh : null;
        var materials = (renderer != null && renderer.sharedMaterial != null) ? renderer.sharedMaterials : null;

        return CheckUpdateGeometryParts(delta, mesh, materials);
    }

    private void OnTransformChildrenChanged()
    {
        // Check all children to see if they have been given a watcher

        for (var c_i = 0; c_i < transform.childCount; c_i++)
        {
            var child = transform.GetChild(c_i).gameObject;
            var watcher = child.GetComponent<NOOWatcher>();

            if (watcher == null)
            {
                // Add it to the child
                child.AddComponent<NOOWatcher>();
            }
        }
    }

    /// <summary>
    /// Return a right-hand transformation matrix for this gameobject
    /// </summary>
    /// <returns></returns>
    private float[] RightHandTransform()
    {
        // Extract position, rotation, and scale
        Vector3 position = transform.localPosition;
        Quaternion rotation = transform.localRotation;
        Vector3 scale = transform.localScale;

        // Create matrix in row-major order
        Matrix4x4 tf_matrix = Matrix4x4.TRS(position, rotation, scale);

        // Convert to a right-handed matrix by flipping the Z axis
        tf_matrix.m02 = -tf_matrix.m02;
        tf_matrix.m12 = -tf_matrix.m12;
        tf_matrix.m20 = -tf_matrix.m20;
        tf_matrix.m21 = -tf_matrix.m21;
        tf_matrix.m23 = -tf_matrix.m23;

        // Extract matrix elements in column-major order
        return new float[]
        {
            tf_matrix.m00, tf_matrix.m10, tf_matrix.m20, tf_matrix.m30,
            tf_matrix.m01, tf_matrix.m11, tf_matrix.m21, tf_matrix.m31,
            tf_matrix.m02, tf_matrix.m12, tf_matrix.m22, tf_matrix.m32,
            tf_matrix.m03, tf_matrix.m13, tf_matrix.m23, tf_matrix.m33
        };
    }
}