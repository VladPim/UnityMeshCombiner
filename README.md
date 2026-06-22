# Unity Mesh Combiner

An editor tool for Unity that merges multiple meshes into a **single combined GameObject** while preserving all original materials. It drastically reduces draw calls (batches), supports an unlimited number of vertices, and is fully undo‑able.

![Unity version](https://img.shields.io/badge/Unity-All%20versions%20(testest%20in%206.3)-brightgreen)
![License](https://img.shields.io/badge/license-MIT-blue)

## Overview

When a scene contains many small objects with MeshRenderers, each one generates at least one draw call – quickly bottlenecking the CPU, especially on mobile or VR platforms.  
**Unity Mesh Combiner** merges all meshes from the selected hierarchy into **one new GameObject**, keeping all materials as separate submeshes. The result: hundreds of draw calls become just a few, dramatically improving rendering performance.

Even if your objects use different materials, the tool creates a **single combined mesh** that contains all materials in the correct order. No material is lost – every original material is present in the final result.

## Features

- **One mesh, all materials** – all objects are combined into a **single GameObject** with a multi‑material mesh. Each unique material gets its own submesh, preserving the exact look.
- **Massive draw call reduction** – same‑material geometry ends up in the same submesh, minimizing the total number of draw calls.
- **Works in all Unity versions** – tested from older releases up to the latest (including Unity 6.3).
- **32‑bit index support** – handles meshes with millions of vertices, no 65k vertex limit.
- **World‑space transformation** – every object's position, rotation, and scale is correctly applied.
- **Original objects are disabled** (not deleted) – you can easily inspect or re‑enable them later.
- **Full Undo / Redo** – every step (creation, disabling) is registered with Unity’s Undo system.
- **Clear logging** – the Console reports exactly how many meshes were combined and how many materials were kept.

## How It Works

1. **Collect all MeshFilters** from the selected GameObjects and their children (only active objects).
2. **Group submeshes by material** – each submesh of every mesh is assigned to the list of the material it uses.
3. **Create temporary meshes per material** – all geometry that shares the same material is merged into one intermediate mesh (vertices already in world space).
4. **Combine into one final mesh** – these intermediate meshes become separate submeshes of the final mesh, preserving the material order.
5. **Spawn a new GameObject** with a MeshFilter and MeshRenderer, assign the combined mesh and the full materials array.
6. **Disable the original objects** so they are no longer rendered.
7. **Select the new object** in the Hierarchy for immediate feedback.

Because the tool groups geometry by material **before** the final combine, the resulting GameObject contains exactly one submesh per unique material – guaranteeing the smallest possible number of draw calls for your scene setup.

## Installation & Usage

1. Copy the `UnityMeshCombiner.cs` script into an `Editor` folder (e.g., `Assets/Editor/`).  
   *The `[MenuItem]` attribute requires the script to be inside an Editor folder, otherwise it won’t compile or appear.*
2. Wait for Unity to compile.
3. In the **Hierarchy**, select one or more root GameObjects.  
   *The tool will also process all children of the selected objects.*
4. From Unity’s top menu, choose **Tools → Combine Meshes**.
5. A new GameObject named `CombinedMesh` appears in the scene.  
   - All original materials are preserved on the new object.  
   - The original objects become inactive but remain in the hierarchy.

## Performance Benefits

- **Draw calls / Batches**: The primary goal. A scene with 500 identical rocks and 10 materials can go from 500 draw calls down to just 10.
- **SetPass calls**: Reduced proportionally because materials are shared.
- **No Static flag required** – works at edit time and creates a real mesh asset you can further edit or export.
- **Mobile / VR friendly** – huge CPU overhead reduction, directly improving frame rate on devices limited by draw‑call throughput.

## Important Limitations

- **Colliders are not preserved** – the new combined GameObject does not have any Collider components. The original objects (and their colliders) are disabled. If you need physics, you can:
  - Add a `MeshCollider` to the combined object (using the combined mesh).
  - Manually place colliders on the new object.

## Compatibility

Works in all modern Unity versions – from the older releases up to the very latest.  
Tested specifically in **Unity 6.3**, but the tool relies only on basic mesh APIs that have been stable for years, so it should work without issues in any version you are using.

## License

MIT – free to use, modify, and distribute in any project.

---

*Happy combining! 🚀*
```
