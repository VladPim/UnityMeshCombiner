using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Unity editor tool that combines multiple meshes into a single mesh
/// to reduce draw calls (batches). It processes the current selection,
/// groups geometry by material to preserve submeshes, and creates a new
/// GameObject with the combined result. Original objects are disabled.
/// Supports any number of vertices by using 32‑bit index buffers.
/// 
/// How it works at a high level:
/// 1. Collects all MeshFilter components from the current selection (including children).
/// 2. For each submesh of each collected mesh, groups it by the material assigned to that submesh.
/// 3. For each unique material, creates a temporary mesh by merging all geometry that uses it.
///    This step already transforms vertices to world space using the original GameObject's transform.
/// 4. Combines all temporary meshes into a final mesh, keeping them as separate submeshes.
/// 5. Creates a new GameObject with the final mesh and the corresponding materials array.
/// 6. Disables the original selected GameObjects so they are no longer rendered.
/// 7. Registers undo operations, allowing the user to revert the whole process.
/// 
/// The key design decision: grouping by material before the final combine.
/// Unity assigns one material per submesh. To minimize draw calls, all triangles
/// that share a material should live in the same submesh. Therefore we first
/// merge same‑material geometry into a single temporary mesh, then combine
/// those temporary meshes as separate submeshes in the final asset.
/// </summary>
public static class UnityMeshCombiner
{
    /// <summary>
    /// Menu item "Tools/Combine Meshes" – entry point for the tool.
    /// Called when the user clicks the menu item in the Unity editor.
    /// </summary>
    [MenuItem("Tools/Combine Meshes")]
    private static void CombineSelectedMeshes()
    {
        // --- 1. Gather all MeshFilter components from the selection hierarchy ---
        // The selection can be a mix of root objects; we want all meshes inside them.
        // If nothing is selected, warn and return.
        if (Selection.gameObjects.Length == 0)
        {
            Debug.LogWarning("No GameObjects selected. Please select at least one object containing MeshFilters.");
            return;
        }

        // List to hold every MeshFilter found in selected objects and their children.
        // We only consider active objects because inactive ones are usually hidden and
        // combining them could lead to unexpected results.
        List<MeshFilter> meshFilters = new List<MeshFilter>();
        foreach (GameObject go in Selection.gameObjects)
        {
            // GetComponentsInChildren<MeshFilter>(true) would include inactive ones,
            // but we skip that to avoid combining objects we can't see.
            MeshFilter[] filtersInChildren = go.GetComponentsInChildren<MeshFilter>();
            meshFilters.AddRange(filtersInChildren);
        }

        // If no MeshFilter components were found at all, log a warning and stop.
        if (meshFilters.Count == 0)
        {
            Debug.LogWarning("No MeshFilter components found in the selected GameObjects or their children.");
            return;
        }

        // --- 2. Group mesh submeshes by their material ---
        // A single mesh may have multiple submeshes, each with a different material.
        // To reduce draw calls, we must keep geometry using the same material together
        // in one submesh of the final combined mesh. So we create a dictionary mapping
        // Material -> list of CombineInstance that use that material.

        // Dictionary: material -> list of CombineInstance (one per submesh part)
        Dictionary<Material, List<CombineInstance>> materialToCombine = new Dictionary<Material, List<CombineInstance>>();

        // List that preserves the order in which materials were first encountered.
        // The final material array must match the submesh order exactly, so we
        // record them in the same sequence we will process later.
        List<Material> materialsOrdered = new List<Material>();

        // Loop through every collected MeshFilter to inspect its submeshes and materials.
        foreach (MeshFilter mf in meshFilters)
        {
            // Skip empty or missing mesh references.
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;

            // Get the MeshRenderer to read the assigned materials.
            MeshRenderer renderer = mf.GetComponent<MeshRenderer>();
            // If no renderer or material array, treat as having zero materials.
            // sharedMaterials returns a copy of the actual array, so it's safe to read.
            Material[] sharedMats = renderer ? renderer.sharedMaterials : new Material[0];

            // A mesh with no materials cannot be rendered; we skip it and warn.
            if (sharedMats.Length == 0)
            {
                Debug.LogWarning($"Skipping '{mf.name}' because it has no materials assigned.", mf);
                continue;
            }

            // Loop over each submesh of the current mesh.
            // Note: mesh.subMeshCount may differ from sharedMats.Length (e.g., if materials
            // were not fully set up). We only process up to the smaller of the two to avoid
            // index errors.
            int subMeshCount = Mathf.Min(mesh.subMeshCount, sharedMats.Length);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material mat = sharedMats[subMeshIndex];

                // A null material in the array is invalid; skip with warning.
                if (mat == null)
                {
                    Debug.LogWarning($"Skipping submesh {subMeshIndex} of '{mf.name}' because material is null.", mf);
                    continue;
                }

                // If this material hasn't been seen yet, create a new list for it
                // and add it to our ordered list to preserve the material order.
                if (!materialToCombine.ContainsKey(mat))
                {
                    materialToCombine[mat] = new List<CombineInstance>();
                    materialsOrdered.Add(mat);
                }

                // Build a CombineInstance for this specific submesh.
                // The transform matrix converts the mesh's vertices from local
                // space to world space (using the object's transform).
                // This is essential because all selected objects may have different
                // positions, rotations, and scales.
                CombineInstance ci = new CombineInstance
                {
                    mesh = mesh,                  // the source mesh
                    subMeshIndex = subMeshIndex,  // which submesh to take
                    transform = mf.transform.localToWorldMatrix // world transform
                };
                // Add this combine instance to the list for the corresponding material.
                materialToCombine[mat].Add(ci);
            }
        }

        // If after skipping all invalid entries we have nothing left, abort.
        if (materialToCombine.Count == 0)
        {
            Debug.LogWarning("No valid meshes with materials were found to combine. Check your selection.");
            return;
        }

        // --- 3. Create the final combined mesh with 32-bit index format ---
        // Unity's default 16-bit index buffers limit a mesh to 65,535 vertices.
        // When merging many objects this limit is easily exceeded. Switching to
        // 32-bit indices supports up to 4 billion vertices, removing this
        // artificial cap and allowing the tool to work with any scene scale.
        Mesh combinedMesh = new Mesh();
        combinedMesh.name = "CombinedMesh";
        combinedMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        // --- 4. For each material group, create an intermediate combined submesh ---
        // We cannot combine all meshes of different materials into one submesh,
        // because Unity assigns one material per submesh. Instead we:
        // 1. Combine all geometry sharing the same material into one temporary mesh.
        // 2. Then combine those temporary meshes into the final mesh as separate submeshes.
        // This way the number of submeshes equals the number of unique materials.

        // List of CombineInstance for the final mesh (one per material group).
        List<CombineInstance> allCombines = new List<CombineInstance>();

        // Final list of materials in the exact order of the submeshes.
        List<Material> finalMaterials = new List<Material>();

        // Process materials in the order they were first seen (preserves original ordering).
        foreach (Material mat in materialsOrdered)
        {
            List<CombineInstance> group = materialToCombine[mat];
            if (group.Count == 0) continue; // safety check (should never happen)

            // Create a temporary mesh for this group. It also needs 32-bit indices
            // because the group itself may contain more than 65,535 vertices.
            Mesh subCombined = new Mesh();
            subCombined.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            // Combine all CombineInstances of this group into subCombined.
            // Parameters:
            //   combine (first) = array of CombineInstance
            //   mergeSubMeshes = true  -> all submeshes within the group are merged
            //                            into a single triangle list, which is what
            //                            we want because they all share the same material.
            //   useMatrices = true     -> use the transform matrices provided in each
            //                            CombineInstance (already set to world transform).
            subCombined.CombineMeshes(group.ToArray(), true, true);

            // Create a CombineInstance that will add this intermediate mesh
            // as one submesh of the final combined mesh. The transform is
            // identity because the vertices are already in world space.
            CombineInstance ci = new CombineInstance
            {
                mesh = subCombined,
                transform = Matrix4x4.identity
            };
            allCombines.Add(ci);
            finalMaterials.Add(mat);
        }

        // --- 5. Final assembly: combine all material groups into one mesh ---
        // mergeSubMeshes = false ensures that each CombineInstance becomes a
        // separate submesh in the final mesh (preserving material boundaries).
        // useMatrices = false because we already applied transforms earlier.
        combinedMesh.CombineMeshes(allCombines.ToArray(), false, false);

        // Recalculate bounds and normals for correct culling and lighting.
        // Note: When mergeSubMeshes is false, Unity may not automatically recalculate
        // normals in some versions, so we explicitly call these methods.
        combinedMesh.RecalculateBounds();
        combinedMesh.RecalculateNormals();
        // Optionally, you could also recalculate tangents if your shaders require them.
        // combinedMesh.RecalculateTangents();

        // --- 6. Create a new GameObject to host the combined mesh ---
        // The new object will be placed at the scene root with a default transform.
        // You can later reparent or move it as needed.
        GameObject combinedObject = new GameObject("CombinedMesh");
        // Register the creation for Undo support (Ctrl+Z to revert).
        // This ensures that the new object can be undone in a single step.
        Undo.RegisterCreatedObjectUndo(combinedObject, "Combine Meshes");

        // Attach MeshFilter and assign the new mesh.
        MeshFilter combinedFilter = combinedObject.AddComponent<MeshFilter>();
        combinedFilter.sharedMesh = combinedMesh;

        // Attach MeshRenderer and assign the materials array in the correct order.
        // The materials array must match the submesh order 1:1.
        MeshRenderer combinedRenderer = combinedObject.AddComponent<MeshRenderer>();
        combinedRenderer.sharedMaterials = finalMaterials.ToArray();

        // --- 7. Disable the original objects to avoid double rendering ---
        // We disable them instead of deleting so the user can easily revert or
        // inspect the original layout. Disabled objects are not rendered,
        // so draw calls drop to only those of the new combined object.
        foreach (GameObject go in Selection.gameObjects)
        {
            // Record the state change for Undo.
            // Using RecordObject so that toggling SetActive can be undone.
            Undo.RecordObject(go, "Disable Original");
            go.SetActive(false);
        }

        // --- 8. Select the newly created object in the Hierarchy ---
        // This gives immediate feedback and allows the user to inspect the result.
        Selection.activeGameObject = combinedObject;

        // Log success with statistics.
        Debug.Log($"Successfully combined {meshFilters.Count} MeshFilter components into " +
                  $"{combinedMesh.subMeshCount} submesh(es) with {finalMaterials.Count} material(s). " +
                  "Original objects have been disabled.");
    }
}