using System;
using System.Collections.Generic;
using UnityEngine;

namespace BasicCutter
{
    /// <summary>One box of a wall's collision, in the wall's local space.</summary>
    [Serializable]
    public struct ColliderBox
    {
        public Vector3 center;
        public Vector3 size;
        public PhysicsMaterial material;
        public bool isTrigger;
    }

    /// <summary>
    /// Marks the child object that holds the box colliders Basic Cutter builds around a wall's openings.
    /// The wall's own Box Colliders are turned off while it exists and turned on again when the wall is
    /// no longer cut. Rebuilt automatically: changes made by hand do not last.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class CutterColliders : MonoBehaviour
    {
        // the wall's own colliders that this replaces (turned off)
        [SerializeField, HideInInspector]
        internal List<BoxCollider> replaced = new List<BoxCollider>();

        [SerializeField, HideInInspector]
        internal string signature = "";
    }
}
