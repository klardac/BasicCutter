using UnityEngine;

namespace BasicCutter
{
    /// <summary>How the cutting volume of a <see cref="MeshCutter"/> is defined.</summary>
    public enum CutterShape
    {
        /// <summary>Exact outer outline of every mesh under this object, seen along the cut axis.</summary>
        Exact = 0,
        /// <summary>Rectangle defined by <see cref="MeshCutter.shapeSize"/>.</summary>
        Box = 1,
        /// <summary>Rectangle with a round or segmental arch on top.</summary>
        Arch = 2,
        /// <summary>Circle / ellipse defined by <see cref="MeshCutter.shapeSize"/>.</summary>
        Circle = 3,
        /// <summary>A closed mesh used as the cutting volume as-is (local space of this object).</summary>
        CustomMesh = 4,
    }

    /// <summary>Local axis the cutter pushes through the wall.</summary>
    public enum CutterAxis
    {
        /// <summary>Thinnest local axis of the window meshes (windows and doors are flat).</summary>
        Auto = 0,
        X = 1,
        Y = 2,
        Z = 3,
    }

    /// <summary>How deep a <see cref="MeshCutter"/> cuts.</summary>
    public enum CutDepth
    {
        /// <summary>Through the whole wall (windows, doors).</summary>
        Through = 0,
        /// <summary>Only into one face of the wall, <see cref="MeshCutter.nicheDepth"/> deep (niches, recesses).</summary>
        Niche = 1,
    }

    /// <summary>Which faces of the wall get a trim around the opening.</summary>
    public enum TrimSides
    {
        /// <summary>Both faces (for a niche: only the open face).</summary>
        Both = 0,
        /// <summary>The face on the cutter's negative cut-axis side.</summary>
        [InspectorName("Negative Axis Side")] Negative = 1,
        /// <summary>The face on the cutter's positive cut-axis side.</summary>
        [InspectorName("Positive Axis Side")] Positive = 2,
    }

    /// <summary>
    /// Put this on a window / door (usually on its prefab). Every closed mesh it overlaps
    /// (walls) gets a hole with the window's shape; the hole's inner faces are rebuilt from the
    /// cutter's faces and use the wall's material. Editor-only workflow: nothing runs at runtime.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Basic Cutter/Mesh Cutter")]
    public sealed class MeshCutter : MonoBehaviour
    {
        [Tooltip("Exact: the exact outline of the window meshes. Box/Arch/Circle: parametric outline. Custom Mesh: a closed mesh used directly.")]
        public CutterShape shape = CutterShape.Exact;

        [Tooltip("Local axis that goes through the wall. Auto picks the thinnest axis of the window meshes.")]
        public CutterAxis axis = CutterAxis.Auto;

        [Tooltip("Grows (+) or shrinks (-) the outline, in local units. A small negative value hides the hole edge behind the frame.")]
        public float padding = 0f;

        [Tooltip("Extra depth added on both sides of the wall so the cut always goes fully through.")]
        [Min(0f)] public float depthMargin = 0.01f;

        [Tooltip("Door mode: Padding is not applied to the bottom edge, and the bottom edge snaps to the wall's bottom " +
                 "(no threshold strip) or to the floor surface when wall and floor are one mesh (no groove). " +
                 "The door's local up axis must point up.")]
        public bool door;

        [Tooltip("Door mode: the largest distance the bottom edge may move to reach the wall's bottom or the floor surface.")]
        [Min(0f)] public float doorBottomSnap = 0.05f;

        [Tooltip("Through: the opening goes through the wall. Niche: a recess in one face of the wall, Niche Depth deep.")]
        public CutDepth depth = CutDepth.Through;

        [Tooltip("Niche: how deep the recess goes into the wall, measured from the wall face.")]
        [Min(0.001f)] public float nicheDepth = 0.15f;

        [Tooltip("Niche: the recess opens on the wall face nearest to the window model. Turn on to use the other face.")]
        public bool nicheFlip;

        [Tooltip("Material of the opening's inner faces. Empty: the wall's own material.")]
        public Material jambMaterial;

        [Tooltip("Builds a frame (trim) around the opening on the wall surface. It follows curved walls.")]
        public bool trim;

        [Tooltip("Which faces of the wall get the trim.")]
        public TrimSides trimSides = TrimSides.Both;

        [Tooltip("Width of the trim around the opening (local units).")]
        [Min(0.001f)] public float trimWidth = 0.08f;

        [Tooltip("How far the trim stands out of the wall surface (local units).")]
        [Min(0.001f)] public float trimThickness = 0.03f;

        [Tooltip("Trim material. Empty: the wall's material.")]
        public Material trimMaterial;

        [Tooltip("Adds a sill under the opening (not for doors).")]
        public bool sill;

        [Tooltip("How much further than the trim the sill stands out of the wall (local units).")]
        [Min(0f)] public float sillDepth = 0.05f;

        [Tooltip("How much wider than the trim the sill is on each side (local units).")]
        [Min(0f)] public float sillOverhang = 0.04f;

        [Tooltip("Only objects on these layers can be cut.")]
        public LayerMask targetLayers = ~0;

        [Tooltip("Width / height of the Box, Arch and Circle shapes (local units).")]
        public Vector2 shapeSize = new Vector2(1f, 1.5f);

        [Tooltip("Offset of the Box, Arch and Circle shapes on the wall plane (local units).")]
        public Vector2 shapeCenter = Vector2.zero;

        [Tooltip("Height of the arch part. Half of the width gives a round arch, less gives a flatter (segmental) arch.")]
        [Min(0.001f)] public float archRise = 0.5f;

        [Tooltip("Number of segments used for arcs.")]
        [Range(3, 128)] public int segments = 24;

        [Tooltip("Closed mesh used as the cutting volume when Shape is Custom Mesh.")]
        public Mesh customMesh;
    }
}
