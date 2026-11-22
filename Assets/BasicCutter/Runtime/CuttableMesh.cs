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

        public Mesh SourceMesh => sourceMesh;
        public Mesh CutMesh => cutMesh;
        public CutState State => state;
    }
}
