using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter
{
    /// <summary>Update behaviour of a cut wall.</summary>
    public enum CutState
    {
        /// <summary>Re-cut automatically whenever a cutter or the wall changes.</summary>
        Live = 0,
        /// <summary>Keep the current cut mesh, ignore further changes (baked).</summary>
        Frozen = 1,
        /// <summary>Show the original mesh and never cut this object.</summary>
        Excluded = 2,
    }

    /// <summary>
    /// Added automatically to every mesh that a <see cref="MeshCutter"/> cuts. Remembers the
    /// original mesh so the cut is non-destructive and can always be reverted.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter))]
    [AddComponentMenu("Basic Cutter/Cuttable Mesh")]
    public sealed class CuttableMesh : MonoBehaviour
    {
        [SerializeField, Tooltip("The original, uncut mesh.")]
        internal Mesh sourceMesh;

        [SerializeField, Tooltip("The generated mesh with the holes.")]
        internal Mesh cutMesh;

        [SerializeField]
        internal CutState state = CutState.Live;

        [SerializeField, HideInInspector]
        internal bool autoAdded;

        [SerializeField, HideInInspector]
        internal string signature = "";

        // jamb materials: how many slots at the end of the renderer's materials were added by the cut
        [SerializeField, HideInInspector]
        internal int addedMaterials;

        // the cutters whose openings are in the current cut mesh (Apply Cuts needs them on baked walls)
        [SerializeField, HideInInspector]
        internal List<MeshCutter> cutBy = new List<MeshCutter>();

        [SerializeField, HideInInspector]
        internal bool cutByKnown;

        // Apply Cuts: sourceMesh then holds the applied result; this is the mesh from before the first apply
        [SerializeField, HideInInspector]
        internal Mesh originalMesh;

        [SerializeField, HideInInspector]
        internal int originalMaterialCount;

        // cutters whose openings were applied into sourceMesh: they no longer cut this wall
        [SerializeField, HideInInspector]
        internal List<MeshCutter> appliedCutters = new List<MeshCutter>();

        // identifies the applied mesh: a prefab that could not store a scene's mesh finds it again by this
        [SerializeField, HideInInspector]
        internal string appliedId = "";

        // a prefab whose applied mesh was not found yet: it shows a temporary cut of originalMesh
        [SerializeField, HideInInspector]
        internal bool appliedMissing;

        // jamb material slots of the applied mesh, set aside while appliedMissing
        [SerializeField, HideInInspector]
        internal List<Material> heldMaterials = new List<Material>();

        public Mesh SourceMesh => sourceMesh;
        public Mesh CutMesh => cutMesh;
        public CutState State => state;

        /// <summary>The mesh before any cut was applied (the same as <see cref="SourceMesh"/> until Apply Cuts is used).</summary>
        public Mesh OriginalMesh => originalMesh != null ? originalMesh : sourceMesh;

        /// <summary>True when cuts were applied into this wall's base mesh.</summary>
        public bool HasAppliedCuts => originalMesh != null;

        /// <summary>True when this prefab's applied mesh was lost and is waiting for its scene to be opened.</summary>
        public bool AppliedMeshMissing => appliedMissing;
    }
}
