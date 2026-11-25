using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// A cut wall's Box Colliders would still block its openings. They are replaced by boxes that leave the
    /// openings out: each opening's box (the bounds of its cut volume in the wall's space) is subtracted
    /// from the wall's boxes, and the pieces are merged again. The boxes live on a child object; the wall's
    /// own colliders are turned off meanwhile and back on when the wall is no longer cut.
    /// </summary>
    internal static class ColliderSplitter
    {
        const string ChildName = "Colliders (Basic Cutter)";
        const float MinSize = 1e-3f;

        public static CutterColliders Find(CuttableMesh cm)
        {
            foreach (Transform child in cm.transform)
                if (child.TryGetComponent(out CutterColliders c)) return c;
            return null;
        }

        /// <summary>The wall-space bounds of an opening's cut volume.</summary>
        public static Bounds HoleBounds(List<VolumeTri> volume)
        {
            var b = new Bounds(volume[0].p0.ToVector3(), Vector3.zero);
            foreach (var t in volume)
            {
                b.Encapsulate(t.p0.ToVector3());
                b.Encapsulate(t.p1.ToVector3());
                b.Encapsulate(t.p2.ToVector3());
            }
            return b;
        }

        /// <summary>
        /// Brings the wall's split colliders up to date with these openings (wall space). Without openings
        /// (and without applied ones) the wall gets its own colliders back.
        /// </summary>
        public static void Update(CuttableMesh cm, List<Bounds> holes)
        {
            if (cm == null) return;
            var existing = Find(cm);
            if (!BasicCutterSettings.instance.splitBoxColliders) { Restore(cm, existing); return; }

            var baseBoxes = BaseBoxes(cm, existing);
            if (baseBoxes.Count == 0 || (holes.Count == 0 && !cm.hasAppliedBoxes)) { Restore(cm, existing); return; }

            var h = Hash64.Create();
            foreach (var b in baseBoxes) { Add(ref h, b.center); Add(ref h, b.size); h.Add(b.isTrigger); h.Add(b.material != null ? b.material.GetInstanceID() : 0); }
            foreach (var b in holes) { Add(ref h, b.min); Add(ref h, b.max); }
            string signature = h.ToString();
            if (existing != null && existing.signature == signature && AllEnabled(existing)) return;

            var boxes = Split(baseBoxes, holes);
            if (existing == null) existing = Create(cm);
            if (!cm.hasAppliedBoxes)
            {
                // the wall's own colliders step aside
                foreach (var own in cm.GetComponents<BoxCollider>())
                    if (own.enabled && !existing.replaced.Contains(own))
                    {
                        existing.replaced.Add(own);
                        own.enabled = false;
                        Changed(own);
                    }
            }
            Write(existing, boxes);
            existing.signature = signature;
            Changed(existing);
        }

        /// <summary>Apply Cuts: the current boxes become the wall's base boxes.</summary>
        public static void CaptureApplied(CuttableMesh cm)
        {
            var existing = Find(cm);
            if (existing == null || !BasicCutterSettings.instance.splitBoxColliders) return;
            cm.appliedBoxes.Clear();
            foreach (var b in existing.GetComponents<BoxCollider>())
                if (b.enabled) cm.appliedBoxes.Add(new ColliderBox { center = b.center, size = b.size, material = b.sharedMaterial, isTrigger = b.isTrigger });
            cm.hasAppliedBoxes = true;
        }

        /// <summary>Discard Applied Cuts: back to the wall's own colliders as the base.</summary>
        public static void ForgetApplied(CuttableMesh cm)
        {
            cm.appliedBoxes.Clear();
            cm.hasAppliedBoxes = false;
            var existing = Find(cm);
            if (existing != null) existing.signature = "";
        }

        /// <summary>The wall's own colliders back on, the split ones removed.</summary>
        public static void Restore(CuttableMesh cm, CutterColliders existing = null)
        {
            existing ??= Find(cm);
            if (existing == null) return;
            RestoreReplaced(existing);
            Remove(existing);
        }

        /// <summary>Split colliders whose wall is no longer cuttable: give the wall its colliders back.</summary>
        public static void CleanUpOrphans(IEnumerable<CutterColliders> all)
        {
            foreach (var c in all.ToList())
            {
                if (c == null) continue;
                var parent = c.transform.parent;
                if (parent != null && parent.GetComponent<CuttableMesh>() != null) continue;
                RestoreReplaced(c);
                Remove(c);
            }
        }

        // ------------------------------------------------------------------ boxes

        static List<ColliderBox> BaseBoxes(CuttableMesh cm, CutterColliders existing)
        {
            if (cm.hasAppliedBoxes) return new List<ColliderBox>(cm.appliedBoxes);
            var list = new List<ColliderBox>();
            IEnumerable<BoxCollider> own = existing != null && existing.replaced.Any(b => b != null)
                ? existing.replaced.Where(b => b != null)
                : cm.GetComponents<BoxCollider>().Where(b => b.enabled);
            foreach (var b in own)
                list.Add(new ColliderBox { center = b.center, size = Abs(b.size), material = b.sharedMaterial, isTrigger = b.isTrigger });
            return list;
        }

        /// <summary>The boxes with every hole taken out, merged back into as few boxes as possible.</summary>
        internal static List<ColliderBox> Split(List<ColliderBox> boxes, List<Bounds> holes)
        {
            var pieces = new List<ColliderBox>(boxes);
            foreach (var hole in holes)
            {
                var next = new List<ColliderBox>();
                foreach (var p in pieces) Subtract(p, hole, next);
                pieces = next;
            }
            Merge(pieces);
            return pieces;
        }

        static void Subtract(ColliderBox box, Bounds hole, List<ColliderBox> output)
        {
            Vector3 aMin = box.center - box.size * 0.5f, aMax = box.center + box.size * 0.5f;
            Vector3 hMin = hole.min, hMax = hole.max;
            if (hMin.x >= aMax.x || hMax.x <= aMin.x || hMin.y >= aMax.y || hMax.y <= aMin.y || hMin.z >= aMax.z || hMax.z <= aMin.z)
            {
                output.Add(box);
                return;
            }
            // slabs around the hole: x on both sides, then y and z inside the remaining x (and y) range
            Vector3 lo = aMin, hi = aMax;
            if (hMin.x > lo.x) Emit(box, new Vector3(lo.x, lo.y, lo.z), new Vector3(hMin.x, hi.y, hi.z), output);
            if (hMax.x < hi.x) Emit(box, new Vector3(hMax.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hi.z), output);
            lo.x = Mathf.Max(lo.x, hMin.x); hi.x = Mathf.Min(hi.x, hMax.x);
            if (hMin.y > lo.y) Emit(box, new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hMin.y, hi.z), output);
            if (hMax.y < hi.y) Emit(box, new Vector3(lo.x, hMax.y, lo.z), new Vector3(hi.x, hi.y, hi.z), output);
            lo.y = Mathf.Max(lo.y, hMin.y); hi.y = Mathf.Min(hi.y, hMax.y);
            if (hMin.z > lo.z) Emit(box, new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, hi.y, hMin.z), output);
            if (hMax.z < hi.z) Emit(box, new Vector3(lo.x, lo.y, hMax.z), new Vector3(hi.x, hi.y, hi.z), output);
        }

        static void Emit(ColliderBox like, Vector3 min, Vector3 max, List<ColliderBox> output)
        {
            var size = max - min;
            if (size.x < MinSize || size.y < MinSize || size.z < MinSize) return;
            output.Add(new ColliderBox { center = (min + max) * 0.5f, size = size, material = like.material, isTrigger = like.isTrigger });
        }

        /// <summary>Joins boxes that share a whole face, until none do.</summary>
        static void Merge(List<ColliderBox> boxes)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < boxes.Count && !merged; i++)
                    for (int j = i + 1; j < boxes.Count && !merged; j++)
                    {
                        if (!TryJoin(boxes[i], boxes[j], out var joined)) continue;
                        boxes[i] = joined;
                        boxes.RemoveAt(j);
                        merged = true;
                    }
            }
        }

        static bool TryJoin(ColliderBox a, ColliderBox b, out ColliderBox joined)
        {
            joined = a;
            if (a.material != b.material || a.isTrigger != b.isTrigger) return false;
            Vector3 aMin = a.center - a.size * 0.5f, aMax = a.center + a.size * 0.5f;
            Vector3 bMin = b.center - b.size * 0.5f, bMax = b.center + b.size * 0.5f;
            const float eps = 1e-4f;
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                if (Mathf.Abs(aMin[u] - bMin[u]) > eps || Mathf.Abs(aMax[u] - bMax[u]) > eps) continue;
                if (Mathf.Abs(aMin[v] - bMin[v]) > eps || Mathf.Abs(aMax[v] - bMax[v]) > eps) continue;
                if (Mathf.Abs(aMax[axis] - bMin[axis]) > eps && Mathf.Abs(bMax[axis] - aMin[axis]) > eps) continue;
                Vector3 min = Vector3.Min(aMin, bMin), max = Vector3.Max(aMax, bMax);
                joined = new ColliderBox { center = (min + max) * 0.5f, size = max - min, material = a.material, isTrigger = a.isTrigger };
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ objects

        static CutterColliders Create(CuttableMesh cm)
        {
            var go = new GameObject(ChildName);
            go.layer = cm.gameObject.layer;
            go.transform.SetParent(cm.transform, false);
            GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(cm.gameObject));
            var marker = go.AddComponent<CutterColliders>();
            Changed(marker);
            return marker;
        }

        static void Write(CutterColliders target, List<ColliderBox> boxes)
        {
            var colliders = target.GetComponents<BoxCollider>().ToList();
            bool inherited = PrefabUtility.IsPartOfPrefabInstance(target);
            while (colliders.Count < boxes.Count) colliders.Add(target.gameObject.AddComponent<BoxCollider>());
            for (int i = 0; i < colliders.Count; i++)
            {
                var c = colliders[i];
                if (i >= boxes.Count)
                {
                    // components that come from a prefab cannot be removed from an instance
                    if (inherited && !PrefabUtility.IsAddedComponentOverride(c)) c.enabled = false;
                    else Object.DestroyImmediate(c);
                    continue;
                }
                var b = boxes[i];
                c.center = b.center;
                c.size = b.size;
                c.sharedMaterial = b.material;
                c.isTrigger = b.isTrigger;
                c.enabled = true;
                Changed(c);
            }
        }

        static bool AllEnabled(CutterColliders c) => c.GetComponents<BoxCollider>().Any() && c.isActiveAndEnabled;

        static void RestoreReplaced(CutterColliders c)
        {
            foreach (var own in c.replaced)
                if (own != null && !own.enabled) { own.enabled = true; Changed(own); }
            c.replaced.Clear();
        }

        static void Remove(CutterColliders c)
        {
            // an object that comes from a prefab cannot be deleted from an instance: switch its colliders off
            if (PrefabUtility.IsPartOfPrefabInstance(c) && !PrefabUtility.IsAddedGameObjectOverride(c.gameObject))
            {
                foreach (var b in c.GetComponents<BoxCollider>())
                    if (b.enabled) { b.enabled = false; Changed(b); }
                c.signature = "";
                Changed(c);
                return;
            }
            var go = c.gameObject;
            CutterSystem.MarkSceneDirty(go);
            Object.DestroyImmediate(go);
        }

        static void Changed(Object o) => CutterSystem.MarkChanged(o);

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static void Add(ref Hash64 h, Vector3 v) { h.Add(v.x); h.Add(v.y); h.Add(v.z); }
    }
}
