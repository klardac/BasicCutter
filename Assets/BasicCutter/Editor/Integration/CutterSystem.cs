using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BasicCutter.EditorTools
{
    /// <summary>
    /// Keeps every wall in the open scene / prefab stage in sync with the cutters that overlap it.
    /// Runs in the editor only: listens to scene changes, re-cuts what changed (throttled while
    /// dragging, large walls on a worker thread), builds trims and finalises meshes (lightmap UVs,
    /// saving) once editing settles.
    /// </summary>
    [InitializeOnLoad]
    internal static class CutterSystem
    {
        /// <summary>How the inspector can fix a reported problem with one click.</summary>
        internal enum IssueFix { None, EnableReadWrite, SelectObject }

        /// <summary>A warning plus the object it is about and how to fix it.</summary>
        internal sealed class Issue
        {
            public string text;
            public IssueFix fix;
            public Object target;
        }

        /// <summary>What the last sync found for a cutter (for inspectors and gizmos).</summary>
        internal sealed class CutterStatus
        {
            public readonly List<Renderer> targets = new List<Renderer>();
            public readonly List<string> warnings = new List<string>();
            public readonly List<Issue> issues = new List<Issue>();

            public void Warn(string text, IssueFix fix = IssueFix.None, Object target = null)
            {
                warnings.Add(text);
                issues.Add(new Issue { text = text, fix = fix, target = target });
            }

            public readonly List<string> infos = new List<string>();
            public CutterOutline outline;
            public double d0 = double.NaN, d1 = double.NaN;
            public double bottomShift;
            public bool niche, nicheNear;
            public bool pending;   // a background cut of one of its walls is still running
            public CutterTrim trim;
        }

        sealed class Placement
        {
            public MeshCutter cutter;
            public CutterOutline outline;
            public Matrix4x4 toWall;
            public CutterPlacement.Result place;
            public ulong hash;
        }

        /// <summary>The objects one sync works on: the current stage, or the contents of a prefab file.</summary>
        sealed class Scope
        {
            public List<MeshCutter> cutters;
            public List<CuttableMesh> walls;
            public List<MeshRenderer> renderers;
            public List<CutterTrim> trims;
            public bool force, background;
            public readonly Dictionary<Mesh, int> refCount = new Dictionary<Mesh, int>();
            public readonly List<Object> toDestroy = new List<Object>();
            // first wall each cutter really cuts: the trim is built on it
            public readonly Dictionary<MeshCutter, (Placement p, MeshFilter wall, SourceMesh sm)> primary =
                new Dictionary<MeshCutter, (Placement, MeshFilter, SourceMesh)>();
            // cutters on baked walls: their trims are kept as they are
            public readonly HashSet<MeshCutter> frozen = new HashSet<MeshCutter>();
        }

        sealed class Job { public string signature; public Task<CutResult> task; }
        sealed class Finished { public string signature; public CutResult result; public string error; }

        /// <summary>Walls with at least this many triangles are cut on a worker thread during live updates.</summary>
        public const int BackgroundThreshold = 1500;
        internal static int BackgroundThresholdOverride = -1; // tests
        static int Threshold => BackgroundThresholdOverride >= 0 ? BackgroundThresholdOverride : BackgroundThreshold;

        static readonly Dictionary<int, CutterStatus> cutterStatus = new Dictionary<int, CutterStatus>();
        static readonly Dictionary<int, List<MeshCutter>> wallCutters = new Dictionary<int, List<MeshCutter>>();
        static readonly HashSet<CuttableMesh> pendingFinalize = new HashSet<CuttableMesh>();
        static readonly HashSet<Mesh> pendingSave = new HashSet<Mesh>();
        static readonly Dictionary<int, Job> jobs = new Dictionary<int, Job>();             // wall MeshFilter id -> running cut
        static readonly Dictionary<int, Finished> finished = new Dictionary<int, Finished>(); // wall MeshFilter id -> result for the next sync
        static bool dirty = true;
        static bool forceAll;
        static double lastChange, lastSync;
        const double FinalizeDelay = 0.6;

        static CutterSystem()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += MarkDirty;
            EditorApplication.update += Update;
            EditorSceneManager.sceneOpened += (s, m) => MarkDirty();
            EditorSceneManager.sceneSaving += (s, p) => FinalizeAll();
            UnityEditor.SceneManagement.PrefabStage.prefabStageOpened += _ => MarkDirty();
            UnityEditor.SceneManagement.PrefabStage.prefabStageClosing += _ => { FinalizeAll(); MarkDirty(); };
            AssemblyReloadEvents.beforeAssemblyReload += FinalizeAll;
            EditorApplication.quitting += FinalizeAll;
        }

        public static CutterStatus GetStatus(MeshCutter c) =>
            c != null && cutterStatus.TryGetValue(c.GetInstanceID(), out var s) ? s : null;

        public static IReadOnlyList<MeshCutter> GetCutters(CuttableMesh cm) =>
            cm != null && wallCutters.TryGetValue(cm.GetInstanceID(), out var l) ? l : (IReadOnlyList<MeshCutter>)Array.Empty<MeshCutter>();

        /// <summary>True while background cuts are running.</summary>
        public static bool IsBusy => jobs.Count > 0;

        public static void MarkDirty()
        {
            dirty = true;
            lastChange = EditorApplication.timeSinceStartup;
        }

        static void OnChangesPublished(ref ObjectChangeEventStream stream) => MarkDirty();

        /// <summary>Re-cuts every wall from scratch.</summary>
        public static void RebuildAll()
        {
            forceAll = true;
            SyncNow();
            FinalizeAll();
        }

        static void Update()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (jobs.Count > 0) PollJobs();
            double now = EditorApplication.timeSinceStartup;
            var settings = BasicCutterSettings.instance;
            if (dirty && settings.liveUpdate && now - lastSync >= settings.updateInterval) SyncInternal(settings.backgroundCutting);
            if ((pendingFinalize.Count > 0 || pendingSave.Count > 0) && jobs.Count == 0 && now - lastChange >= FinalizeDelay) FinalizeAll();
        }

        // ------------------------------------------------------------------ background jobs

        static void PollJobs()
        {
            List<int> done = null;
            foreach (var kv in jobs)
                if (kv.Value.task.IsCompleted) (done ??= new List<int>()).Add(kv.Key);
            if (done == null) return;
            foreach (int id in done)
            {
                finished[id] = Collect(jobs[id]);
                jobs.Remove(id);
            }
            // the next sync applies the results (if they are still what the scene wants)
            dirty = true;
            SceneView.RepaintAll();
        }

        static Finished Collect(Job job)
        {
            var f = new Finished { signature = job.signature };
            try { f.result = job.task.Result; }
            catch (AggregateException e) { f.error = (e.InnerException ?? e).Message; }
            catch (Exception e) { f.error = e.Message; }
            return f;
        }

        /// <summary>Waits for running background cuts and applies them.</summary>
        static void CompleteJobs()
        {
            if (jobs.Count == 0) return;
            foreach (var kv in jobs.ToList())
            {
                try { kv.Value.task.Wait(); } catch { /* reported by Collect */ }
                finished[kv.Key] = Collect(kv.Value);
            }
            jobs.Clear();
            SyncInternal(false);
        }

        // ------------------------------------------------------------------ sync

        /// <summary>Brings all walls of the current stage up to date immediately (no background work).</summary>
        public static void SyncNow() => SyncInternal(false);

        /// <summary>Tests: one sync as the live update runs it.</summary>
        internal static void SyncForTests(bool background) => SyncInternal(background);

        static void MarkSceneDirty(GameObject go)
        {
            var scene = go.scene;
            if (!scene.IsValid()) return;
            // prefab mode is a preview scene that must be marked; prefab contents loaded by code are not
            if (EditorSceneManager.IsPreviewScene(scene) && PrefabStageUtility.GetPrefabStage(go) == null) return;
            EditorSceneManager.MarkSceneDirty(scene);
        }

        static void SyncInternal(bool background)
        {
            dirty = false;
            lastSync = EditorApplication.timeSinceStartup;
            bool force = forceAll;
            forceAll = false;
            try
            {
                var stage = StageUtility.GetCurrentStageHandle();
                var scope = new Scope
                {
                    cutters = stage.FindComponentsOfType<MeshCutter>().Where(c => c != null && c.isActiveAndEnabled).ToList(),
                    walls = stage.FindComponentsOfType<CuttableMesh>().Where(c => c != null).ToList(),
                    trims = stage.FindComponentsOfType<CutterTrim>().Where(c => c != null).ToList(),
                    force = force,
                    background = background,
                };
                cutterStatus.Clear();
                wallCutters.Clear();
                if (scope.cutters.Count == 0 && scope.walls.Count == 0 && scope.trims.Count == 0) return;
                scope.renderers = scope.cutters.Count == 0 ? new List<MeshRenderer>() :
                    stage.FindComponentsOfType<MeshRenderer>().Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).ToList();
                Run(scope);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        /// <summary>Cuts the contents of a prefab file loaded with LoadPrefabContents; meshes go into that file.</summary>
        internal static void CutPrefabContents(GameObject root, string prefabPath)
        {
            var frozen = new List<CuttableMesh>();
            foreach (var cm in root.GetComponentsInChildren<CuttableMesh>(true))
            {
                // a baked wall that lost its mesh is cut once more from its current windows
                var mf = cm.GetComponent<MeshFilter>();
                if (cm.state == CutState.Frozen && (cm.cutMesh == null || mf == null || mf.sharedMesh == null)) { cm.state = CutState.Live; frozen.Add(cm); }
                if (cm.cutMesh == null) cm.signature = "";
            }
            var scope = new Scope
            {
                cutters = root.GetComponentsInChildren<MeshCutter>(true).Where(c => c.isActiveAndEnabled).ToList(),
                walls = root.GetComponentsInChildren<CuttableMesh>(true).ToList(),
                trims = root.GetComponentsInChildren<CutterTrim>(true).ToList(),
                renderers = root.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.enabled && r.gameObject.activeInHierarchy).ToList(),
            };
            GeneratedMeshStore.PrefabPathOverride = prefabPath;
            try { Run(scope); }
            finally { GeneratedMeshStore.PrefabPathOverride = null; }
            foreach (var cm in frozen) if (cm != null) cm.state = CutState.Frozen;
            MarkDirty();
        }

        static void Run(Scope scope)
        {
            // ---- find what every cutter goes through
            var perWall = new Dictionary<MeshFilter, List<Placement>>();
            foreach (var cutter in scope.cutters)
            {
                var status = new CutterStatus();
                cutterStatus[cutter.GetInstanceID()] = status;
                var outline = CutterShapeBuilder.Get(cutter);
                status.outline = outline;
                if (!outline.IsValid) { status.Warn(outline.error ?? "Invalid cutter shape."); continue; }
                var bounds = WorldBounds(cutter, outline);
                bool nicheReported = false;

                foreach (var r in scope.renderers)
                {
                    if (r == null || !r.bounds.Intersects(bounds)) continue;
                    if (((1 << r.gameObject.layer) & cutter.targetLayers.value) == 0) continue;
                    if (r.GetComponentInParent<MeshCutter>(true) != null) continue; // windows, doors and their trims
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null) continue;
                    var cm = r.GetComponent<CuttableMesh>();
                    if (cm != null && cm.state == CutState.Excluded) continue;
                    var source = SourceOf(cm, mf);
                    if (source == null) continue;

                    SourceMesh sm;
                    try { sm = SourceMesh.Get(source); }
                    catch (Exception e)
                    {
                        bool unreadable = !source.isReadable;
                        status.Warn($"{r.name}: {e.Message}", unreadable ? IssueFix.EnableReadWrite : IssueFix.None, unreadable ? source : null);
                        continue;
                    }
                    if (sm.TriangleCount == 0) continue;

                    var toWall = r.transform.worldToLocalMatrix * cutter.transform.localToWorldMatrix;
                    var place = CutterPlacement.Evaluate(outline, toWall, sm, cutter.depthMargin, cutter.door ? cutter.doorBottomSnap : 0);
                    if (!place.hit) continue;
                    if (!sm.Closed)
                    {
                        status.Warn($"'{r.name}' is not a closed mesh ({sm.OpenEdges} open edges) and was skipped.", IssueFix.SelectObject, r.gameObject);
                        continue;
                    }
                    if (cutter.depth == CutDepth.Niche)
                    {
                        CutterPlacement.ApplyNiche(ref place, outline, cutter.nicheDepth, cutter.nicheFlip);
                        if (place.nicheTooDeep && !nicheReported)
                        {
                            status.infos.Add($"Niche Depth is not less than the thickness of '{r.name}', so the cut goes through.");
                            nicheReported = true;
                        }
                    }

                    var h = Hash64.Create();
                    h.Add(outline.hash); h.Add(toWall); h.Add(place.d0); h.Add(place.d1); h.Add(place.bottomShift);
                    if (!perWall.TryGetValue(mf, out var list)) perWall[mf] = list = new List<Placement>();
                    list.Add(new Placement { cutter = cutter, outline = outline, toWall = toWall, place = place, hash = h.Value });
                }
            }

            // ---- overlapping cutters on the same wall merge into one opening (the boolean handles it)
            foreach (var kv in perWall)
            {
                var list = kv.Value;
                list.Sort((a, b) => a.cutter.GetInstanceID().CompareTo(b.cutter.GetInstanceID()));
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (list[i].cutter == list[j].cutter || !Overlaps(list[i], list[j], kv.Key.transform)) continue;
                        GetStatus(list[i].cutter)?.infos.Add($"Merges with '{list[j].cutter.name}' on '{kv.Key.name}' into one opening.");
                        GetStatus(list[j].cutter)?.infos.Add($"Merges with '{list[i].cutter.name}' on '{kv.Key.name}' into one opening.");
                    }
            }

            // ---- apply
            foreach (var cm in scope.walls)
                if (cm != null && cm.cutMesh != null) Count(scope.refCount, cm.cutMesh);
            foreach (var t in scope.trims)
                if (t != null && t.TryGetComponent(out MeshFilter tmf) && tmf.sharedMesh != null) Count(scope.refCount, tmf.sharedMesh);

            var handled = new HashSet<MeshFilter>();
            foreach (var kv in perWall)
            {
                handled.Add(kv.Key);
                ApplyWall(scope, kv.Key, kv.Value);
            }
            foreach (var cm in scope.walls)
            {
                if (cm == null) continue;
                var mf = cm.GetComponent<MeshFilter>();
                if (mf == null || handled.Contains(mf)) continue;
                if (!cm.isActiveAndEnabled && cm.state != CutState.Excluded) continue; // inactive walls are left alone
                ApplyWall(scope, mf, null);
            }

            UpdateTrims(scope);

            foreach (var o in scope.toDestroy)
                if (o != null) Object.DestroyImmediate(o);
        }

        static void Count(Dictionary<Mesh, int> refCount, Mesh m)
        {
            refCount.TryGetValue(m, out int c);
            refCount[m] = c + 1;
        }

        static Mesh SourceOf(CuttableMesh cm, MeshFilter mf)
        {
            if (cm == null) return mf.sharedMesh;
            if (cm.sourceMesh == null) return mf.sharedMesh;
            // the user replaced the mesh by hand: that is the new source
            if (mf.sharedMesh != null && mf.sharedMesh != cm.sourceMesh && mf.sharedMesh != cm.cutMesh) return mf.sharedMesh;
            return cm.sourceMesh;
        }

        static void ApplyWall(Scope scope, MeshFilter mf, List<Placement> placements)
        {
            var cm = mf.GetComponent<CuttableMesh>();
            bool hasCutters = placements != null && placements.Count > 0;

            if (cm != null)
            {
                var cutterList = new List<MeshCutter>();
                if (hasCutters) foreach (var p in placements) cutterList.Add(p.cutter);
                wallCutters[cm.GetInstanceID()] = cutterList;
                var newSource = SourceOf(cm, mf);
                if (newSource != cm.sourceMesh) { cm.sourceMesh = newSource; cm.signature = ""; MarkChanged(cm); }
            }

            if (cm != null && cm.state == CutState.Frozen)
            {
                if (hasCutters) foreach (var p in placements) scope.frozen.Add(p.cutter);
                return;
            }
            if (cm != null && cm.state == CutState.Excluded) { Restore(cm, mf); return; }

            if (!hasCutters)
            {
                if (cm == null) return;
                Restore(cm, mf);
                if (cm.autoAdded && CanRemove(cm)) scope.toDestroy.Add(cm);
                return;
            }

            bool added = false;
            if (cm == null)
            {
                cm = mf.gameObject.AddComponent<CuttableMesh>();
                cm.sourceMesh = mf.sharedMesh;
                cm.autoAdded = true;
                added = true;
                wallCutters[cm.GetInstanceID()] = placements.Select(p => p.cutter).ToList();
            }

            var sm = SourceMesh.Get(cm.sourceMesh);
            var renderer = mf.GetComponent<Renderer>();

            // ---- jamb materials: extra submeshes with their own material slots after the wall's own
            var current = renderer != null ? renderer.sharedMaterials : Array.Empty<Material>();
            int addedCount = Mathf.Clamp(cm.addedMaterials, 0, current.Length);
            var baseMaterials = current.Take(current.Length - addedCount).ToArray();
            var extras = new List<Material>();
            int[] jambSub = null;
            if (placements.Any(x => x.cutter.jambMaterial != null))
            {
                int subCount = Math.Max(1, sm.subMeshCount);
                if (renderer == null || baseMaterials.Length != subCount)
                {
                    foreach (var p in placements.Where(x => x.cutter.jambMaterial != null))
                        GetStatus(p.cutter)?.Warn($"'{mf.name}' has {baseMaterials.Length} material slot(s) for {subCount} submesh(es), so Jamb Material is not used there.", IssueFix.SelectObject, mf.gameObject);
                }
                else
                {
                    jambSub = new int[placements.Count];
                    for (int i = 0; i < placements.Count; i++)
                    {
                        var m = placements[i].cutter.jambMaterial;
                        if (m == null) { jambSub[i] = -1; continue; }
                        int k = extras.IndexOf(m);
                        if (k < 0) { extras.Add(m); k = extras.Count - 1; }
                        jambSub[i] = subCount + k;
                    }
                }
            }

            var h = Hash64.Create();
            h.Add(sm.contentHash);
            var order = Enumerable.Range(0, placements.Count).OrderBy(i => placements[i].hash).ToList();
            foreach (int i in order) { h.Add(placements[i].hash); h.Add(jambSub != null ? jambSub[i] : -1); }
            string signature = h.ToString();

            foreach (var p in placements)
            {
                var st = GetStatus(p.cutter);
                if (st == null) continue;
                st.targets.Add(renderer);
                if (st.targets.Count == 1)
                {
                    st.bottomShift = p.place.bottomShift;
                    st.niche = p.place.niche; st.nicheNear = p.place.nicheNear;
                }
                if (double.IsNaN(st.d0) || p.place.d0 < st.d0) st.d0 = p.place.d0;
                if (double.IsNaN(st.d1) || p.place.d1 > st.d1) st.d1 = p.place.d1;
            }

            if (!scope.force && !added && cm.signature == signature && cm.cutMesh != null && mf.sharedMesh == cm.cutMesh)
            {
                SetMaterials(cm, renderer, baseMaterials, extras);
                foreach (var p in placements) RecordPrimary(scope, p, mf, sm);
                return;
            }

            // ---- the cut: a result a worker already computed, a background job, or right here
            int id = mf.GetInstanceID();
            CutResult result = null;
            string error = null;
            if (finished.TryGetValue(id, out var fin))
            {
                finished.Remove(id);
                if (fin.signature == signature && !scope.force) { result = fin.result; error = fin.error; }
            }
            if (result == null && error == null)
            {
                if (!scope.background && jobs.TryGetValue(id, out var running) && running.signature == signature && !scope.force)
                {
                    jobs.Remove(id);
                    try { running.task.Wait(); } catch { /* reported by Collect */ }
                    var f = Collect(running);
                    result = f.result; error = f.error;
                }
                else if (scope.background && sm.TriangleCount >= Threshold)
                {
                    // a job for an older state may still run: its result is dropped and the next sync starts this one
                    if (!jobs.ContainsKey(id))
                    {
                        var volumes = Volumes(placements);
                        var jamb = jambSub;
                        jobs[id] = new Job { signature = signature, task = Task.Run(() => CutEngine.Cut(sm, volumes, jamb)) };
                    }
                    foreach (var p in placements)
                    {
                        var st = GetStatus(p.cutter);
                        if (st != null) st.pending = true;
                        RecordPrimary(scope, p, mf, sm);
                    }
                    return;
                }
                else
                {
                    try { result = CutEngine.Cut(sm, Volumes(placements), jambSub); }
                    catch (Exception e) { error = e.Message; }
                }
            }

            if (error != null || result == null)
            {
                error ??= "no result";
                Debug.LogWarning($"[Basic Cutter] Cutting '{mf.name}' failed: {error}", mf);
                foreach (var p in placements) GetStatus(p.cutter)?.Warn($"Cutting '{mf.name}' failed: {error}", IssueFix.SelectObject, mf.gameObject);
                return;
            }

            for (int i = 0; i < placements.Count; i++)
            {
                if (!result.cutterAffected[i]) GetStatus(placements[i].cutter)?.targets.Remove(renderer);
                else RecordPrimary(scope, placements[i], mf, sm);
            }

            if (result.AffectedCount == 0)
            {
                Restore(cm, mf);
                if (cm.autoAdded && CanRemove(cm)) scope.toDestroy.Add(cm);
                return;
            }

            var mesh = GeneratedMeshStore.Acquire(cm, cm.cutMesh, "_Cut", GeneratedMeshStore.IsInheritedFromPrefab(cm, "cutMesh"), scope.refCount);
            cm.cutMesh = mesh;
            result.WriteTo(mesh, sm);
            EditorUtility.SetDirty(mesh);
            SetMesh(mf, cm, mesh);
            SetMaterials(cm, renderer, baseMaterials, extras);
            cm.signature = signature;
            MarkChanged(cm);
            pendingFinalize.Add(cm);
        }

        static List<List<VolumeTri>> Volumes(List<Placement> placements)
        {
            var volumes = new List<List<VolumeTri>>(placements.Count);
            foreach (var p in placements)
                volumes.Add(CutterPlacement.VolumeInWallSpace(p.outline, p.toWall, p.place.d0, p.place.d1, p.place.bottomShift));
            return volumes;
        }

        static void RecordPrimary(Scope scope, Placement p, MeshFilter mf, SourceMesh sm)
        {
            if (!scope.primary.ContainsKey(p.cutter)) scope.primary[p.cutter] = (p, mf, sm);
        }

        static bool CanRemove(CuttableMesh cm) =>
            !PrefabUtility.IsPartOfPrefabInstance(cm) || PrefabUtility.IsAddedComponentOverride(cm);

        static void SetMesh(MeshFilter mf, CuttableMesh cm, Mesh mesh)
        {
            var old = mf.sharedMesh;
            if (old != mesh)
            {
                mf.sharedMesh = mesh;
                MarkChanged(mf);
            }
            var col = mf.GetComponent<MeshCollider>();
            if (col != null && (col.sharedMesh == null || col.sharedMesh == old || col.sharedMesh == cm.sourceMesh || col.sharedMesh == mesh))
            {
                col.sharedMesh = null;
                col.sharedMesh = mesh;
                MarkChanged(col);
            }
        }

        /// <summary>Wall materials followed by the jamb materials the cut added.</summary>
        static void SetMaterials(CuttableMesh cm, Renderer renderer, Material[] baseMaterials, List<Material> extras)
        {
            if (renderer == null) return;
            var want = baseMaterials.Concat(extras).ToArray();
            if (!renderer.sharedMaterials.SequenceEqual(want))
            {
                renderer.sharedMaterials = want;
                MarkChanged(renderer);
            }
            if (cm.addedMaterials != extras.Count)
            {
                cm.addedMaterials = extras.Count;
                MarkChanged(cm);
            }
        }

        static void Restore(CuttableMesh cm, MeshFilter mf)
        {
            if (cm.addedMaterials > 0)
            {
                var r = mf.GetComponent<Renderer>();
                if (r != null)
                {
                    var cur = r.sharedMaterials;
                    r.sharedMaterials = cur.Take(Math.Max(0, cur.Length - cm.addedMaterials)).ToArray();
                    MarkChanged(r);
                }
                cm.addedMaterials = 0;
                MarkChanged(cm);
            }
            if (cm.sourceMesh == null) return;
            if (mf.sharedMesh != cm.sourceMesh) SetMesh(mf, cm, cm.sourceMesh);
            if (!string.IsNullOrEmpty(cm.signature)) { cm.signature = ""; MarkChanged(cm); }
        }

        static void MarkChanged(Object o)
        {
            EditorUtility.SetDirty(o);
            if (PrefabUtility.IsPartOfPrefabInstance(o)) PrefabUtility.RecordPrefabInstancePropertyModifications(o);
            if (o is Component c) MarkSceneDirty(c.gameObject);
        }

        // ------------------------------------------------------------------ trims

        const string TrimName = "Trim (Basic Cutter)";

        static void UpdateTrims(Scope scope)
        {
            var keep = new HashSet<CutterTrim>();
            foreach (var cutter in scope.cutters)
            {
                if (cutter == null || !cutter.trim || cutter.shape == CutterShape.CustomMesh) continue;
                if (!scope.primary.TryGetValue(cutter, out var prim))
                {
                    // baked wall: the trim stays exactly as it was baked
                    var baked = scope.frozen.Contains(cutter) ? FindTrim(cutter) : null;
                    if (baked != null) keep.Add(baked);
                    continue;
                }
                var st = GetStatus(cutter);
                var settings = new TrimSettings
                {
                    nearSide = cutter.trimSides != TrimSides.Positive,
                    farSide = cutter.trimSides != TrimSides.Negative,
                    width = cutter.trimWidth,
                    thickness = cutter.trimThickness,
                    sill = cutter.sill,
                    sillDepth = cutter.sillDepth,
                    sillOverhang = cutter.sillOverhang,
                };
                var place = prim.p.place;
                var h = Hash64.Create();
                h.Add(prim.p.hash); h.Add(prim.sm.contentHash);
                h.Add(place.wallNear); h.Add(place.wallFar); h.Add(place.niche); h.Add(place.nicheNear);
                h.Add(settings.nearSide); h.Add(settings.farSide); h.Add(settings.width); h.Add(settings.thickness);
                h.Add(settings.sill); h.Add(settings.sillDepth); h.Add(settings.sillOverhang);
                string signature = h.ToString();

                var trim = FindTrim(cutter) ?? CreateTrim(cutter);
                keep.Add(trim);
                if (st != null) st.trim = trim;
                var tmf = trim.GetComponent<MeshFilter>();
                var tr = trim.GetComponent<MeshRenderer>();

                if (scope.force || trim.signature != signature || tmf.sharedMesh == null)
                {
                    TrimMeshData data;
                    try { data = TrimBuilder.Build(prim.p.outline, place, prim.p.toWall, prim.sm, settings); }
                    catch (Exception e) { st?.Warn("Trim failed: " + e.Message); continue; }
                    if (data.IsEmpty) st?.infos.Add("The trim is empty: Trim Sides points at the closed face of the niche.");
                    var mesh = GeneratedMeshStore.Acquire(trim, tmf.sharedMesh, "", GeneratedMeshStore.IsInheritedFromPrefab(tmf, "m_Mesh"), scope.refCount);
                    data.WriteTo(mesh);
                    EditorUtility.SetDirty(mesh);
                    if (tmf.sharedMesh != mesh) { tmf.sharedMesh = mesh; MarkChanged(tmf); }
                    trim.signature = signature;
                    MarkChanged(trim);
                    pendingSave.Add(mesh);
                }

                var wallRenderer = prim.wall != null ? prim.wall.GetComponent<Renderer>() : null;
                var mat = cutter.trimMaterial != null ? cutter.trimMaterial : wallRenderer != null ? wallRenderer.sharedMaterial : null;
                if (tr.sharedMaterial != mat) { tr.sharedMaterial = mat; MarkChanged(tr); }
                if (!tr.enabled) { tr.enabled = true; MarkChanged(tr); }
            }

            foreach (var t in scope.trims)
            {
                if (t == null || keep.Contains(t)) continue;
                // a trim that comes from a prefab asset cannot be deleted from an instance: hide it
                if (PrefabUtility.IsPartOfPrefabInstance(t) && !PrefabUtility.IsAddedGameObjectOverride(t.gameObject))
                {
                    var r = t.GetComponent<MeshRenderer>();
                    if (r != null && r.enabled) { r.enabled = false; MarkChanged(r); }
                    continue;
                }
                scope.toDestroy.Add(t.gameObject);
                MarkSceneDirty(t.gameObject);
            }
        }

        static CutterTrim FindTrim(MeshCutter cutter)
        {
            foreach (Transform child in cutter.transform)
                if (child.TryGetComponent(out CutterTrim t)) return t;
            return null;
        }

        static CutterTrim CreateTrim(MeshCutter cutter)
        {
            var go = new GameObject(TrimName);
            go.layer = cutter.gameObject.layer;
            go.transform.SetParent(cutter.transform, false);
            GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(cutter.gameObject));
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            var t = go.AddComponent<CutterTrim>();
            MarkSceneDirty(go);
            return t;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>World bounds that a wall must touch to be a candidate for this cutter.</summary>
        internal static Bounds WorldBounds(MeshCutter cutter, CutterOutline o)
        {
            var local = new List<D3>();
            if (o.IsCustom)
            {
                foreach (var t in o.customVolume) { local.Add(t.p0); local.Add(t.p1); local.Add(t.p2); }
            }
            else
            {
                double tol = Math.Max(0.02, 0.25 * (o.windowDepthMax - o.windowDepthMin));
                foreach (var loop in o.loops)
                    foreach (var p in loop)
                    {
                        local.Add(o.ToLocal(p.x, p.y, o.windowDepthMin - tol));
                        local.Add(o.ToLocal(p.x, p.y, o.windowDepthMax + tol));
                    }
            }
            var m = cutter.transform.localToWorldMatrix;
            var b = new Bounds(m.MultiplyPoint3x4(local[0].ToVector3()), Vector3.zero);
            foreach (var p in local) b.Encapsulate(m.MultiplyPoint3x4(p.ToVector3()));
            return b;
        }

        static bool Overlaps(Placement a, Placement b, Transform wall)
        {
            // world bounds of the actual prisms
            Bounds Prism(Placement p)
            {
                var tris = CutterPlacement.VolumeInWallSpace(p.outline, p.toWall, p.place.d0, p.place.d1, p.place.bottomShift);
                var m = wall.localToWorldMatrix;
                var bb = new Bounds(m.MultiplyPoint3x4(tris[0].p0.ToVector3()), Vector3.zero);
                foreach (var t in tris)
                {
                    bb.Encapsulate(m.MultiplyPoint3x4(t.p0.ToVector3()));
                    bb.Encapsulate(m.MultiplyPoint3x4(t.p1.ToVector3()));
                    bb.Encapsulate(m.MultiplyPoint3x4(t.p2.ToVector3()));
                }
                return bb;
            }
            var ba = Prism(a); var bbx = Prism(b);
            ba.Expand(-1e-4f); bbx.Expand(-1e-4f);
            if (!ba.Intersects(bbx)) return false;
            if (a.outline.IsCustom || b.outline.IsCustom) return true;

            // same facing: exact 2D test in a's outline plane
            var bToA = a.cutter.transform.worldToLocalMatrix * b.cutter.transform.localToWorldMatrix;
            var bAxisInA = new DMatrix(bToA).Vector(b.outline.AxisVector).Normalized;
            if (Math.Abs(D3.Dot(bAxisInA, a.outline.AxisVector)) < 0.98) return true;
            var dm = new DMatrix(bToA);
            double mid = (b.place.d0 + b.place.d1) * 0.5;
            foreach (var la in a.outline.loops)
                foreach (var lbRaw in b.outline.loops)
                {
                    var lb = new List<D2>(lbRaw.Count);
                    foreach (var p in lbRaw) lb.Add(a.outline.Project(dm.Point(b.outline.ToLocal(p.x, p.y, mid))));
                    if (PolygonsOverlap(la, lb)) return true;
                }
            return false;
        }

        static bool PolygonsOverlap(List<D2> a, List<D2> b)
        {
            for (int i = 0; i < a.Count; i++)
            {
                D2 p1 = a[i], p2 = a[(i + 1) % a.Count];
                for (int j = 0; j < b.Count; j++)
                {
                    D2 q1 = b[j], q2 = b[(j + 1) % b.Count];
                    double d = D2.Cross(p2 - p1, q2 - q1);
                    if (Math.Abs(d) < 1e-15) continue;
                    double t = D2.Cross(q1 - p1, q2 - q1) / d, u = D2.Cross(q1 - p1, p2 - p1) / d;
                    if (t > 1e-9 && t < 1 - 1e-9 && u > 1e-9 && u < 1 - 1e-9) return true;
                }
            }
            return Inside(a, b[0]) || Inside(b, a[0]);
        }

        static bool Inside(List<D2> poly, D2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                D2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------------ finalize

        /// <summary>Finishes background cuts, generates lightmap UVs where needed and writes changed meshes to disk.</summary>
        public static void FinalizeAll()
        {
            CompleteJobs();
            if (pendingFinalize.Count == 0 && pendingSave.Count == 0) return;
            var list = pendingFinalize.ToList();
            pendingFinalize.Clear();
            var mode = BasicCutterSettings.instance.lightmapUvs;
            foreach (var cm in list)
            {
                if (cm == null || cm.cutMesh == null) continue;
                var mesh = cm.cutMesh;
                bool uv2 = mode == LightmapUvMode.Always;
                if (mode == LightmapUvMode.Auto)
                {
                    bool sourceHasUv2 = cm.sourceMesh != null && cm.sourceMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1);
                    uv2 = sourceHasUv2 || GameObjectUtility.AreStaticEditorFlagsSet(cm.gameObject, StaticEditorFlags.ContributeGI);
                }
                if (uv2)
                {
                    try { Unwrapping.GenerateSecondaryUVSet(mesh); }
                    catch (Exception e) { Debug.LogWarning($"[Basic Cutter] Lightmap UVs for '{cm.name}' failed: {e.Message}", cm); }
                }
                GeneratedMeshStore.Save(mesh);
            }
            var saves = pendingSave.ToList();
            pendingSave.Clear();
            foreach (var m in saves) GeneratedMeshStore.Save(m);
        }

        /// <summary>Forces walls to be re-cut on the next sync (used by inspectors).</summary>
        public static void Invalidate(IEnumerable<CuttableMesh> walls)
        {
            foreach (var w in walls) if (w != null) w.signature = "";
            MarkDirty();
            SyncNow();
        }
    }
}
