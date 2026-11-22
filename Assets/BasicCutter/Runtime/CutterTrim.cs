using UnityEngine;

namespace BasicCutter
{
    /// <summary>
    /// Marks the trim object a <see cref="MeshCutter"/> generates around its opening. Created and
    /// removed automatically; editing it by hand has no lasting effect.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public sealed class CutterTrim : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        internal string signature = "";
    }
}
