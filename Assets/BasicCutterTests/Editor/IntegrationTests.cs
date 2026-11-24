using System.Collections.Generic;
using System.Linq;
using BasicCutter.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BasicCutter.Tests
{
    public class IntegrationTests
    {
        const string TempFolder = "Assets/BasicCutterTestTemp";
        string oldFolder;
        bool oldLive, oldBackground;
        MeshStorage oldStorage;

        [SetUp]
        public void SetUp()
        {
            var s = BasicCutterSettings.instance;
            oldFolder = s.outputFolder; oldLive = s.liveUpdate; oldStorage = s.meshStorage; oldBackground = s.backgroundCutting;
            s.outputFolder = TempFolder + "/Generated";
            s.liveUpdate = false; // tests drive the sync explicitly
            s.meshStorage = MeshStorage.AssetFolder; // most tests check the asset files; embedded storage has its own tests
            wallAsset = frameAsset = null;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            var s = BasicCutterSettings.instance;
            s.outputFolder = oldFolder; s.liveUpdate = oldLive; s.meshStorage = oldStorage; s.backgroundCutting = oldBackground;
            CutterSystem.BackgroundThresholdOverride = -1;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.DeleteAsset(TempFolder);
        }

        static GameObject MakeObject(string name, Mesh mesh, Vector3 pos, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();
            return go;
        }

        static GameObject MakeWindow(Vector3 pos)
        {
            var root = new GameObject("Window");
            root.transform.position = pos;
            root.AddComponent<MeshCutter>();
            MakeObject("Frame", ProceduralMeshes.Frame(ProceduralMeshes.Rect(1f, 1.2f), ProceduralMeshes.Rect(0.9f, 1.1f), 0.1f), pos, root.transform);
            root.transform.GetChild(0).localPosition = Vector3.zero;
            return root;
        }

        static double Volume(Mesh m) => SourceMesh.Read(m).Volume();

        [Test]
        public void Window_CutsWall_AndRevertsWhenMovedAway()
        {
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0.5f, 1.6f, 0));

            CutterSystem.SyncNow();

            var cm = wall.GetComponent<CuttableMesh>();
            Assert.IsNotNull(cm, "wall should become cuttable");
            var mf = wall.GetComponent<MeshFilter>();
            Assert.AreNotSame(wallMesh, mf.sharedMesh);
            Assert.AreSame(wallMesh, cm.SourceMesh);
            Assert.IsTrue(AssetDatabase.Contains(mf.sharedMesh), "cut mesh is saved as an asset");
            var cut = SourceMesh.Read(mf.sharedMesh);
            Assert.IsTrue(cut.IsClosed(out int open), $"{open} open edges");
            Assert.AreEqual(4 * 3 * 0.3 - 1.0 * 1.2 * 0.3, cut.Volume(), 1e-3);

            // moving the window updates the cut in place
            window.transform.position = new Vector3(-1f, 1.6f, 0);
            CutterSystem.SyncNow();
            Assert.AreSame(cm.CutMesh, mf.sharedMesh);
            Assert.AreEqual(4 * 3 * 0.3 - 1.0 * 1.2 * 0.3, Volume(mf.sharedMesh), 1e-3);

            // moving it away restores the original and removes the helper component
            window.transform.position = new Vector3(20f, 1.6f, 0);
            CutterSystem.SyncNow();
            Assert.AreSame(wallMesh, mf.sharedMesh);
            Assert.IsNull(wall.GetComponent<CuttableMesh>());
        }

        [Test]
        public void DisablingCutter_RestoresWall()
        {
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            CutterSystem.SyncNow();
            Assert.AreNotSame(wallMesh, wall.GetComponent<MeshFilter>().sharedMesh);
            window.GetComponent<MeshCutter>().enabled = false;
            CutterSystem.SyncNow();
            Assert.AreSame(wallMesh, wall.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void Floor_UnderDoor_IsNotCut()
        {
            var floorMesh = ProceduralMeshes.Box(new Vector3(10, 0.2f, 10), new Vector3(0, -0.1f, 0), "Floor");
            var floor = MakeObject("Floor", floorMesh, Vector3.zero);
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var door = new GameObject("Door");
            door.AddComponent<MeshCutter>();
            MakeObject("Leaf", ProceduralMeshes.Box(new Vector3(1f, 2.2f, 0.1f), new Vector3(0, 1.1f, 0), "Leaf"), Vector3.zero, door.transform);

            CutterSystem.SyncNow();

            Assert.IsNull(floor.GetComponent<CuttableMesh>());
            Assert.AreSame(floorMesh, floor.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsNotNull(wall.GetComponent<CuttableMesh>());
            Assert.IsTrue(SourceMesh.Read(wall.GetComponent<MeshFilter>().sharedMesh).IsClosed(out _));
        }

        [Test]
        public void DuplicatedWall_GetsItsOwnMesh()
        {
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wallA = MakeObject("Wall A", wallMesh, new Vector3(0, 1.5f, 0));
            MakeWindow(new Vector3(0, 1.5f, 0));
            CutterSystem.SyncNow();
            var meshA = wallA.GetComponent<MeshFilter>().sharedMesh;
            double volA = Volume(meshA);

            // duplicate wall + a window at a different spot on the copy
            var wallB = Object.Instantiate(wallA);
            wallB.name = "Wall B";
            wallB.transform.position = new Vector3(0, 1.5f, 10f);
            MakeWindow(new Vector3(1.2f, 1.2f, 10f));
            CutterSystem.SyncNow();

            var meshB = wallB.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotSame(meshA, meshB, "the copy must not overwrite the original's mesh");
            Assert.AreSame(meshA, wallA.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreEqual(volA, Volume(meshA), 1e-6);
        }

        [Test]
        public void OverlappingWindows_MergeIntoOneOpening()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var a = MakeWindow(new Vector3(0, 1.5f, 0));
            var b = MakeWindow(new Vector3(0.5f, 1.5f, 0));
            CutterSystem.SyncNow();
            var stA = CutterSystem.GetStatus(a.GetComponent<MeshCutter>());
            var stB = CutterSystem.GetStatus(b.GetComponent<MeshCutter>());
            Assert.AreEqual(1, stA.targets.Count);
            Assert.AreEqual(1, stB.targets.Count);
            Assert.IsTrue(stA.infos.Any(w => w.Contains("Merges")));
            var cut = SourceMesh.Read(wall.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(cut.IsClosed(out _));
            Assert.AreEqual(4 * 3 * 0.3 - 1.5 * 1.2 * 0.3, cut.Volume(), 1e-3, "one 1.5 m wide opening");
        }

        [Test]
        public void EmbeddedStorage_KeepsMeshInTheScene()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0.5f, 1.6f, 0));
            CutterSystem.SyncNow();
            var mf = wall.GetComponent<MeshFilter>();
            var mesh = mf.sharedMesh;
            Assert.AreNotSame(wallMesh, mesh);
            Assert.IsFalse(EditorUtility.IsPersistent(mesh), "no asset file");
            window.transform.position = new Vector3(-1f, 1.6f, 0);
            CutterSystem.SyncNow();
            Assert.AreSame(mesh, mf.sharedMesh, "updated in place");
            Assert.IsTrue(SourceMesh.Read(mesh).IsClosed(out _));
        }

        [Test]
        public void EmbeddedStorage_NewPrefab_GetsMeshesInsideTheFile()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            AssetDatabase.CreateFolder("Assets", "BasicCutterTestTemp");
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            AssetDatabase.CreateAsset(wallMesh, TempFolder + "/Wall.asset");
            var frame = ProceduralMeshes.Frame(ProceduralMeshes.Rect(1f, 1.2f), ProceduralMeshes.Rect(0.9f, 1.1f), 0.1f, "Frame");
            AssetDatabase.CreateAsset(frame, TempFolder + "/Frame.asset");
            var house = new GameObject("House");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0), house.transform);
            var window = new GameObject("Window");
            window.transform.SetParent(house.transform, false);
            window.transform.localPosition = new Vector3(0.5f, 1.6f, 0);
            var cutter = window.AddComponent<MeshCutter>();
            cutter.trim = true;
            MakeObject("Frame", frame, Vector3.zero, window.transform).transform.localPosition = Vector3.zero;
            CutterSystem.SyncNow();
            Assert.IsFalse(EditorUtility.IsPersistent(wall.GetComponent<MeshFilter>().sharedMesh));

            string path = TempFolder + "/House.prefab";
            PrefabUtility.SaveAsPrefabAsset(house, path);
            PrefabMeshFixer.Fix(path); // normally runs right after the import

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var assetWall = asset.transform.Find("Wall").GetComponent<MeshFilter>().sharedMesh;
            Assert.IsNotNull(assetWall, "the prefab has its cut mesh");
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(assetWall), "stored inside the prefab file");
            Assert.AreEqual(4 * 3 * 0.3 - 1.0 * 1.2 * 0.3, SourceMesh.Read(assetWall).Volume(), 1e-3);
            var assetTrim = asset.GetComponentInChildren<CutterTrim>(true);
            Assert.IsNotNull(assetTrim);
            Assert.IsNotNull(assetTrim.GetComponent<MeshFilter>().sharedMesh, "the trim mesh is stored too");
            Assert.IsFalse(PrefabMeshFixer.NeedsFix(asset));
        }

        [Test]
        public void JambMaterial_AddsASlot_AndRevertRemovesIt()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var wallMat = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "WallMat" };
            var jambMat = new Material(wallMat) { name = "JambMat" };
            var renderer = wall.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = wallMat;
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            window.GetComponent<MeshCutter>().jambMaterial = jambMat;
            CutterSystem.SyncNow();
            CollectionAssert.AreEqual(new[] { wallMat, jambMat }, renderer.sharedMaterials);
            var mesh = wall.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(2, mesh.subMeshCount);
            Assert.AreEqual(8, mesh.GetTriangles(1).Length / 3);

            // a second sync must not add the slot again
            CutterSystem.RebuildAll();
            Assert.AreEqual(2, renderer.sharedMaterials.Length);

            window.transform.position = new Vector3(20, 1.5f, 0);
            CutterSystem.SyncNow();
            CollectionAssert.AreEqual(new[] { wallMat }, renderer.sharedMaterials);
        }

        [Test]
        public void Trim_IsCreated_IgnoredBySilhouette_AndRemoved()
        {
            MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            var cutter = window.GetComponent<MeshCutter>();
            double area = Poly2D.SignedArea(CutterShapeBuilder.Get(cutter).loops[0]);
            cutter.trim = true;
            CutterSystem.SyncNow();
            var trim = window.GetComponentInChildren<CutterTrim>();
            Assert.IsNotNull(trim, "trim object created under the window");
            Assert.IsNotNull(trim.GetComponent<MeshFilter>().sharedMesh);
            Assert.AreEqual(area, Poly2D.SignedArea(CutterShapeBuilder.Get(cutter).loops[0]), 1e-9, "the trim does not grow the opening");

            cutter.trim = false;
            CutterSystem.SyncNow();
            Assert.IsNull(window.GetComponentInChildren<CutterTrim>(), "trim removed");
        }

        [Test]
        public void BakedWall_KeepsItsTrim()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            window.GetComponent<MeshCutter>().trim = true;
            CutterSystem.SyncNow();
            wall.GetComponent<CuttableMesh>().state = CutState.Frozen;
            CutterSystem.SyncNow();
            Assert.IsNotNull(window.GetComponentInChildren<CutterTrim>());
        }

        [Test]
        public void Niche_ThroughTheSystem()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var niche = new GameObject("Niche");
            niche.transform.position = new Vector3(0, 1.5f, 0);
            var c = niche.AddComponent<MeshCutter>();
            c.shape = CutterShape.Box;
            c.axis = CutterAxis.Z;
            c.shapeSize = new Vector2(0.8f, 1f);
            c.depth = CutDepth.Niche;
            c.nicheDepth = 0.1f;
            CutterSystem.SyncNow();
            var st = CutterSystem.GetStatus(c);
            Assert.IsTrue(st.niche);
            var cut = SourceMesh.Read(wall.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(cut.IsClosed(out _));
            Assert.AreEqual(4 * 3 * 0.3 - 0.8 * 1.0 * 0.1, cut.Volume(), 1e-3);
        }

        [Test]
        public void BackgroundCutting_AppliesTheResultLater()
        {
            CutterSystem.BackgroundThresholdOverride = 0; // every wall counts as large
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            CutterSystem.SyncForTests(background: true);
            Assert.IsTrue(CutterSystem.IsBusy || wall.GetComponent<MeshFilter>().sharedMesh == wallMesh, "the cut is not applied synchronously");
            CutterSystem.FinalizeAll(); // waits for the worker and applies the result
            Assert.IsFalse(CutterSystem.IsBusy);
            var mesh = wall.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreNotSame(wallMesh, mesh);
            Assert.AreEqual(4 * 3 * 0.3 - 1.0 * 1.2 * 0.3, Volume(mesh), 1e-3);

            // a result for an outdated position is dropped
            window.transform.position = new Vector3(1f, 1.5f, 0);
            CutterSystem.SyncForTests(background: true);
            window.transform.position = new Vector3(-1f, 1.5f, 0);
            CutterSystem.FinalizeAll();
            CutterSystem.SyncNow();
            var cut = SourceMesh.Read(wall.GetComponent<MeshFilter>().sharedMesh);
            // the hole is now around x = -1: a ray through x = -1 hits nothing
            var hits = new List<SourceMesh.Hit>();
            cut.Raycast(new D3(-1, 0, -1), new D3(0, 0, 1), hits);
            Assert.AreEqual(0, hits.Count, "the latest window position wins");
        }

        [Test]
        public void PrefabWithWallAndWindow_InstancesShareMesh_UntilOverridden()
        {
            AssetDatabase.CreateFolder("Assets", "BasicCutterTestTemp");
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            AssetDatabase.CreateAsset(wallMesh, TempFolder + "/Wall.asset");
            var frame = ProceduralMeshes.Frame(ProceduralMeshes.Rect(1f, 1.2f), ProceduralMeshes.Rect(0.9f, 1.1f), 0.1f, "Frame");
            AssetDatabase.CreateAsset(frame, TempFolder + "/Frame.asset");

            var house = new GameObject("House");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0), house.transform);
            var window = new GameObject("Window");
            window.transform.SetParent(house.transform, false);
            window.transform.localPosition = new Vector3(0.5f, 1.6f, 0);
            window.AddComponent<MeshCutter>();
            MakeObject("Frame", frame, Vector3.zero, window.transform).transform.localPosition = Vector3.zero;
            CutterSystem.SyncNow();
            CutterSystem.FinalizeAll();
            var prefabCut = wall.GetComponent<MeshFilter>().sharedMesh;
            Assert.IsTrue(AssetDatabase.Contains(prefabCut));

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(house, TempFolder + "/House.prefab", InteractionMode.AutomatedAction);
            var second = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            second.transform.position = new Vector3(10, 0, 5);
            second.transform.rotation = Quaternion.Euler(0, 40, 0);
            CutterSystem.SyncNow();

            var secondWall = second.transform.Find("Wall").GetComponent<MeshFilter>();
            Assert.AreSame(prefabCut, secondWall.sharedMesh, "moving the whole prefab must not re-cut");

            // move the window inside the second instance only (an override)
            var secondWindow = second.transform.Find("Window");
            secondWindow.localPosition = new Vector3(-1f, 1.4f, 0);
            CutterSystem.SyncNow();
            Assert.AreNotSame(prefabCut, secondWall.sharedMesh, "the overridden instance needs its own mesh");
            Assert.AreSame(prefabCut, wall.GetComponent<MeshFilter>().sharedMesh, "the first instance keeps the prefab's mesh");
            Assert.IsTrue(SourceMesh.Read(secondWall.sharedMesh).IsClosed(out _));
        }

        [Test]
        public void DemoScene_BuildsAndCutsAllWalls()
        {
            var env = DemoScene.Build(TempFolder + "/Demo");
            var walls = env.GetComponentsInChildren<CuttableMesh>();
            Assert.AreEqual(3, walls.Length, "Wall A, Wall B and the curved wall are cut; the floor is not");
            foreach (var w in walls)
            {
                var mesh = w.GetComponent<MeshFilter>().sharedMesh;
                Assert.AreSame(w.CutMesh, mesh, w.name);
                // positions must match bit-for-bit (no hairline cracks), not just within a tolerance
                Assert.IsTrue(SourceMesh.Read(mesh).IsClosed(out int open, 1e-9), $"{w.name}: {open} open edges");
                Assert.Less(SourceMesh.Read(mesh).Volume(), SourceMesh.Read(w.SourceMesh).Volume() - 0.05, w.name);
            }
            foreach (var c in Object.FindObjectsByType<MeshCutter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                var st = CutterSystem.GetStatus(c);
                Assert.IsNotNull(st, c.name);
                Assert.AreEqual(1, st.targets.Count, $"{c.name} should cut exactly one wall. Warnings: {string.Join("; ", st.warnings)}");
            }
        }

        [Test]
        public void AssetFolder_NewCutMesh_SurvivesAReimport()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var window = MakeWindow(new Vector3(0, 1.5f, 0));
            window.GetComponent<MeshCutter>().trim = true;
            CutterSystem.SyncNow();
            var mf = wall.GetComponent<MeshFilter>();
            string path = AssetDatabase.GetAssetPath(mf.sharedMesh);
            Assert.IsNotEmpty(path);
            // the editor may unload an asset and load it again from its file: the file must already hold
            // the cut, not the empty mesh it was created with
            Resources.UnloadAsset(mf.sharedMesh);
            var reloaded = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            Assert.AreEqual(WallVolume - HoleVolume, SourceMesh.Read(reloaded).Volume(), 1e-3);
            var trimMesh = window.GetComponentInChildren<CutterTrim>().GetComponent<MeshFilter>().sharedMesh;
            string trimPath = AssetDatabase.GetAssetPath(trimMesh);
            Resources.UnloadAsset(trimMesh);
            Assert.Greater(AssetDatabase.LoadAssetAtPath<Mesh>(trimPath).vertexCount, 0, "trim mesh saved too");
        }

        // ------------------------------------------------------------------ apply cuts

        const double WallVolume = 4 * 3 * 0.3, HoleVolume = 1.0 * 1.2 * 0.3;

        static void AssertHoles(GameObject wall, int holes, string message)
        {
            var sm = SourceMesh.Read(wall.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(sm.IsClosed(out int open), $"{message}: {open} open edges");
            Assert.AreEqual(WallVolume - holes * HoleVolume, sm.Volume(), 1e-3, message);
        }

        [Test]
        public void ApplyCuts_BakedWall_KeepsOldHole_AndNewWindowCuts()
        {
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            var a = MakeWindow(new Vector3(-1f, 1.5f, 0));
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            cm.state = CutState.Frozen;

            // a new window on a baked wall does not cut it and says why
            var b = MakeWindow(new Vector3(1f, 1.5f, 0));
            CutterSystem.SyncNow();
            AssertHoles(wall, 1, "baked: only the old hole");
            var stA = CutterSystem.GetStatus(a.GetComponent<MeshCutter>());
            var stB = CutterSystem.GetStatus(b.GetComponent<MeshCutter>());
            Assert.AreEqual(1, stA.baked.Count, "A is baked into the wall");
            Assert.IsTrue(stB.warnings.Any(w => w.Contains("is baked")), "B explains why it does not cut");

            Assert.AreEqual(1, CutterSystem.ApplyCuts(new[] { cm }));
            Assert.AreEqual(CutState.Live, cm.State);
            Assert.IsTrue(cm.HasAppliedCuts);
            Assert.AreSame(wallMesh, cm.OriginalMesh);
            CollectionAssert.AreEqual(new[] { a.GetComponent<MeshCutter>() }, cm.appliedCutters, "only the baked window is applied");
            AssertHoles(wall, 2, "after Apply Cuts the new window cuts too");
            Assert.AreEqual(1, CutterSystem.GetStatus(a.GetComponent<MeshCutter>()).applied.Count);

            // the applied hole stays when its window is deleted; the live one follows its window
            Object.DestroyImmediate(a);
            b.transform.position = new Vector3(1.3f, 1.5f, 0);
            CutterSystem.SyncNow();
            AssertHoles(wall, 2, "old hole kept, new one moved");
            Assert.IsNotNull(wall.GetComponent<CuttableMesh>());

            // with no live window left the wall shows the applied mesh and keeps its component
            Object.DestroyImmediate(b);
            CutterSystem.SyncNow();
            Assert.IsNotNull(wall.GetComponent<CuttableMesh>(), "the component remembers the original mesh");
            AssertHoles(wall, 1, "only the applied hole");

            Assert.AreEqual(1, CutterSystem.DiscardAppliedCuts(new[] { cm }));
            Assert.IsFalse(cm.HasAppliedCuts);
            Assert.AreSame(wallMesh, wall.GetComponent<MeshFilter>().sharedMesh, "back to the original");
        }

        [Test]
        public void ApplyCuts_LiveWall_MovingTheAppliedWindowNoLongerChangesIt()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var a = MakeWindow(new Vector3(-1f, 1.5f, 0));
            a.GetComponent<MeshCutter>().trim = true;
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            var trimMesh = a.GetComponentInChildren<CutterTrim>().GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(1, CutterSystem.ApplyCuts(new[] { cm }));
            var applied = wall.GetComponent<MeshFilter>().sharedMesh;
            // (asset meshes are compared as Unity objects: a reload gives a new C# wrapper for the same mesh)
            Assert.AreEqual(cm.SourceMesh, applied);
            AssertHoles(wall, 1, "the applied mesh has the hole");
            Assert.AreEqual(1, CutterSystem.GetStatus(a.GetComponent<MeshCutter>()).applied.Count);

            a.transform.position = new Vector3(1f, 1.5f, 0);
            CutterSystem.SyncNow();
            Assert.AreEqual(applied, wall.GetComponent<MeshFilter>().sharedMesh, "not cut again");
            AssertHoles(wall, 1, "one hole, where it was applied");
            var trim = a.GetComponentInChildren<CutterTrim>();
            Assert.IsNotNull(trim, "the applied window keeps its trim");
            Assert.AreEqual(trimMesh, trim.GetComponent<MeshFilter>().sharedMesh);
        }

        [Test]
        public void ApplyCuts_CanBeUndone()
        {
            var wallMesh = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
            var wall = MakeObject("Wall", wallMesh, new Vector3(0, 1.5f, 0));
            MakeWindow(new Vector3(-1f, 1.5f, 0));
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            var cut = cm.CutMesh;
            Undo.IncrementCurrentGroup();
            CutterSystem.ApplyCuts(new[] { cm });
            Assert.IsTrue(cm.HasAppliedCuts);
            Undo.PerformUndo();
            CutterSystem.SyncNow();
            Assert.IsFalse(cm.HasAppliedCuts);
            Assert.AreSame(wallMesh, cm.SourceMesh);
            Assert.AreSame(cut, wall.GetComponent<MeshFilter>().sharedMesh);
            AssertHoles(wall, 1, "live again");
        }

        [Test]
        public void ApplyCuts_WallBakedByAnOlderVersion_FindsItsWindows()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var a = MakeWindow(new Vector3(-1.3f, 1.5f, 0));
            var a2 = MakeWindow(new Vector3(0f, 1.5f, 0));
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            cm.state = CutState.Frozen;
            // 1.0.0 did not record the windows of a cut
            cm.cutBy.Clear();
            cm.cutByKnown = false;

            // a window placed on the baked wall before the upgrade: not part of the baked cut
            var b = MakeWindow(new Vector3(1.3f, 1.5f, 0));
            CutterSystem.SyncNow();
            CollectionAssert.AreEquivalent(new[] { a.GetComponent<MeshCutter>(), a2.GetComponent<MeshCutter>() }, cm.cutBy);
            AssertHoles(wall, 2, "still the baked cut");

            CutterSystem.ApplyCuts(new[] { cm });
            CollectionAssert.DoesNotContain(cm.appliedCutters, b.GetComponent<MeshCutter>());
            Assert.AreEqual(1, CutterSystem.GetStatus(b.GetComponent<MeshCutter>()).targets.Count, "the new window cuts");
            AssertHoles(wall, 3, "two applied, one live");
        }

        [Test]
        public void ApplyCuts_KeepsJambMaterial_AndDiscardRemovesItsSlot()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var wallMat = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "WallMat" };
            var jambMat = new Material(wallMat) { name = "JambMat" };
            var renderer = wall.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = wallMat;
            var a = MakeWindow(new Vector3(-1f, 1.5f, 0));
            a.GetComponent<MeshCutter>().jambMaterial = jambMat;
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            CutterSystem.ApplyCuts(new[] { cm });
            CollectionAssert.AreEqual(new[] { wallMat, jambMat }, renderer.sharedMaterials, "the slot belongs to the applied mesh");
            Assert.AreEqual(0, cm.addedMaterials);

            var b = MakeWindow(new Vector3(1f, 1.5f, 0));
            b.GetComponent<MeshCutter>().jambMaterial = jambMat;
            CutterSystem.SyncNow();
            CollectionAssert.AreEqual(new[] { wallMat, jambMat, jambMat }, renderer.sharedMaterials);
            AssertHoles(wall, 2, "both");

            CutterSystem.DiscardAppliedCuts(new[] { cm });
            CollectionAssert.AreEqual(new[] { wallMat, jambMat }, renderer.sharedMaterials, "the original slot plus one jamb slot for A and B");
            Assert.AreEqual(1, cm.addedMaterials);
            AssertHoles(wall, 2, "A cuts live again after the discard");
        }

        [Test]
        public void CutLiveAgain_KeepsTheOldHole_AndCutsWhereTheWindowIsNow()
        {
            var wall = MakeObject("Wall", ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall"), new Vector3(0, 1.5f, 0));
            var a = MakeWindow(new Vector3(-1f, 1.5f, 0));
            CutterSystem.SyncNow();
            var cm = wall.GetComponent<CuttableMesh>();
            CutterSystem.ApplyCuts(new[] { cm });
            a.transform.position = new Vector3(1f, 1.5f, 0);
            CutterSystem.SyncNow();
            AssertHoles(wall, 1, "applied: the window does not cut");

            var cutter = a.GetComponent<MeshCutter>();
            Assert.AreEqual(1, CutterSystem.CutLiveAgain(cutter));
            CollectionAssert.DoesNotContain(cm.appliedCutters, cutter);
            Assert.IsTrue(cm.HasAppliedCuts, "the applied mesh stays");
            AssertHoles(wall, 2, "old hole kept, new one cut");
            Assert.AreEqual(1, CutterSystem.GetStatus(cutter).targets.Count);
            Assert.AreEqual(0, CutterSystem.GetStatus(cutter).applied.Count);

            a.transform.position = new Vector3(1.3f, 1.5f, 0);
            CutterSystem.SyncNow();
            AssertHoles(wall, 2, "the live hole follows the window");
        }

        /// <summary>The demo's Wall B with its door, as in the editor: Embedded storage, a prefab door, Ctrl+D and Delete.</summary>
        static (CuttableMesh wall, GameObject door) DemoWallB()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            DemoScene.Build(TempFolder + "/Demo");
            var wall = GameObject.Find("Wall B").GetComponent<CuttableMesh>();
            return (wall, GameObject.Find("Door"));
        }

        static double WallVolumeOf(CuttableMesh cm)
        {
            var sm = SourceMesh.Read(cm.GetComponent<MeshFilter>().sharedMesh);
            Assert.IsTrue(sm.IsClosed(out int open), $"{open} open edges");
            return sm.Volume();
        }

        const double DoorHole = 1.0 * 2.2 * 0.3; // about: the cut also reaches the wall bottom

        [Test]
        public void Demo_AppliedDoor_DuplicateMovedAndOriginalDeleted_KeepsBothHoles()
        {
            var (wall, door) = DemoWallB();
            CutterSystem.SyncNow();
            CutterSystem.ApplyCuts(new[] { wall });
            double applied = WallVolumeOf(wall);

            // Ctrl+D, then move the copy along the wall
            Selection.activeGameObject = door;
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            var copy = Selection.activeGameObject;
            Assert.AreNotSame(door, copy);
            copy.transform.position = wall.transform.TransformPoint(new Vector3(2.55f, -1.5f, 0));
            CutterSystem.SyncNow();
            double withCopy = WallVolumeOf(wall);
            Assert.AreEqual(applied - DoorHole, withCopy, 0.05, "the copy cuts a second door hole");

            // Delete the original door (as the Delete key does)
            Undo.DestroyObjectImmediate(door);
            CutterSystem.SyncNow();
            Assert.AreEqual(withCopy, WallVolumeOf(wall), 1e-6, "the old door hole stays");
        }

        [Test]
        public void Demo_AppliedDoor_CutLiveAgain_KeepsTheOldHole()
        {
            var (wall, door) = DemoWallB();
            CutterSystem.SyncNow();
            CutterSystem.ApplyCuts(new[] { wall });
            double applied = WallVolumeOf(wall);

            door.transform.position = wall.transform.TransformPoint(new Vector3(2.55f, -1.5f, 0));
            CutterSystem.SyncNow();
            Assert.AreEqual(applied, WallVolumeOf(wall), 1e-6, "applied: moving the door changes nothing");

            Assert.AreEqual(1, CutterSystem.CutLiveAgain(door.GetComponent<MeshCutter>()));
            Assert.AreEqual(applied - DoorHole, WallVolumeOf(wall), 0.05, "old hole kept, the door cuts where it is now");
        }

        // ------------------------------------------------------------------ applied meshes in prefabs (Embedded)

        Mesh wallAsset, frameAsset;

        /// <summary>A house (wall + window) whose meshes are asset files, as a prefab's would be.</summary>
        GameObject MakeHouse(Vector3 windowPos)
        {
            if (wallAsset == null)
            {
                if (!AssetDatabase.IsValidFolder(TempFolder)) AssetDatabase.CreateFolder("Assets", "BasicCutterTestTemp");
                wallAsset = ProceduralMeshes.Box(new Vector3(4, 3, 0.3f), Vector3.zero, "Wall");
                AssetDatabase.CreateAsset(wallAsset, TempFolder + "/Wall.asset");
                frameAsset = ProceduralMeshes.Frame(ProceduralMeshes.Rect(1f, 1.2f), ProceduralMeshes.Rect(0.9f, 1.1f), 0.1f, "Frame");
                AssetDatabase.CreateAsset(frameAsset, TempFolder + "/Frame.asset");
            }
            var house = new GameObject("House");
            MakeObject("Wall", wallAsset, new Vector3(0, 1.5f, 0), house.transform).transform.localPosition = new Vector3(0, 1.5f, 0);
            var window = new GameObject("Window");
            window.transform.SetParent(house.transform, false);
            window.transform.localPosition = windowPos;
            window.AddComponent<MeshCutter>();
            MakeObject("Frame", frameAsset, Vector3.zero, window.transform).transform.localPosition = Vector3.zero;
            return house;
        }

        static CuttableMesh AssetWall(string path) =>
            AssetDatabase.LoadAssetAtPath<GameObject>(path).transform.Find("Wall").GetComponent<CuttableMesh>();

        [Test]
        public void ApplyCuts_EmbeddedInScene_PrefabSavedFromCode_KeepsTheAppliedMesh()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            var house = MakeHouse(new Vector3(0.5f, 1.6f, 0));
            CutterSystem.SyncNow();
            var cm = house.GetComponentInChildren<CuttableMesh>();
            CutterSystem.ApplyCuts(new[] { cm });
            Assert.IsNotEmpty(cm.appliedId);
            // the window is gone: cutting the prefab again could not bring the hole back
            Object.DestroyImmediate(house.transform.Find("Window").gameObject);
            CutterSystem.SyncNow();

            string path = TempFolder + "/House.prefab";
            PrefabUtility.SaveAsPrefabAsset(house, path); // from code, not connected to the scene object
            Assert.IsTrue(PrefabMeshFixer.NeedsFix(AssetDatabase.LoadAssetAtPath<GameObject>(path)), "the scene mesh could not be referenced");
            PrefabMeshFixer.Fix(path);

            var assetCm = AssetWall(path);
            Assert.IsTrue(assetCm.HasAppliedCuts);
            Assert.IsFalse(assetCm.AppliedMeshMissing);
            Assert.AreEqual(cm.appliedId, assetCm.appliedId);
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(assetCm.SourceMesh), "the applied mesh is inside the prefab file");
            var shown = assetCm.GetComponent<MeshFilter>().sharedMesh;
            Assert.AreEqual(WallVolume - HoleVolume, SourceMesh.Read(shown).Volume(), 1e-3, "the applied hole is kept");
            Assert.IsFalse(PrefabMeshFixer.NeedsFix(AssetDatabase.LoadAssetAtPath<GameObject>(path)));
        }

        [Test]
        public void ApplyAll_FromOneOfTwoInstances_StoresThatInstancesAppliedMesh()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            string path = TempFolder + "/House.prefab";
            var source = MakeHouse(new Vector3(-1f, 1.5f, 0));
            PrefabUtility.SaveAsPrefabAsset(source, path);
            Object.DestroyImmediate(source);
            PrefabMeshFixer.Fix(path);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var i1 = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var i2 = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            i2.transform.position = new Vector3(0, 0, 10);
            i2.transform.Find("Window").localPosition = new Vector3(1f, 1.5f, 0); // its hole is somewhere else
            CutterSystem.SyncNow();
            var cm1 = i1.GetComponentInChildren<CuttableMesh>();
            var cm2 = i2.GetComponentInChildren<CuttableMesh>();
            CutterSystem.ApplyCuts(new[] { cm1, cm2 });
            Assert.AreNotEqual(cm1.appliedId, cm2.appliedId);
            ulong hash1 = SourceMesh.Read(cm1.SourceMesh).contentHash, hash2 = SourceMesh.Read(cm2.SourceMesh).contentHash;
            Assert.AreNotEqual(hash1, hash2);

            PrefabUtility.ApplyPrefabInstance(i1, InteractionMode.AutomatedAction);
            PrefabMeshFixer.Fix(path); // normally runs right after the import

            var assetCm = AssetWall(path);
            Assert.AreEqual(cm1.appliedId, assetCm.appliedId, "the ID came with Apply All");
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(assetCm.SourceMesh));
            Assert.AreEqual(hash1, SourceMesh.Read(assetCm.SourceMesh).contentHash, "the mesh of the instance that was applied, not the other one");
            Assert.AreEqual(WallVolume - HoleVolume, SourceMesh.Read(assetCm.GetComponent<MeshFilter>().sharedMesh).Volume(), 1e-3);
        }

        [Test]
        public void AppliedMesh_NotLoaded_PrefabWaits_AndIsRepairedWhenItsSceneOpens()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            var house = MakeHouse(new Vector3(0.5f, 1.6f, 0));
            CutterSystem.SyncNow();
            CutterSystem.ApplyCuts(new[] { house.GetComponentInChildren<CuttableMesh>() });
            Object.DestroyImmediate(house.transform.Find("Window").gameObject);
            CutterSystem.SyncNow();
            string scenePath = TempFolder + "/Applied.unity";
            Assert.IsTrue(EditorSceneManager.SaveScene(house.scene, scenePath));
            string path = TempFolder + "/House.prefab";
            PrefabUtility.SaveAsPrefabAsset(house, path);

            // the scene closes and the session forgets the mesh before the prefab is fixed
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AppliedMeshes.ClearForTests();
            EditorUtility.UnloadUnusedAssetsImmediate();
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Open the scene where Apply Cuts was used"));
            PrefabMeshFixer.Fix(path);

            var assetCm = AssetWall(path);
            Assert.IsTrue(assetCm.AppliedMeshMissing, "waits for its scene");
            Assert.AreEqual(WallVolume, SourceMesh.Read(assetCm.GetComponent<MeshFilter>().sharedMesh).Volume(), 1e-3, "temporary: the original (no window left)");
            CollectionAssert.Contains(AppliedMeshes.PendingPrefabs, path);
            Assert.IsFalse(PrefabMeshFixer.NeedsFix(AssetDatabase.LoadAssetAtPath<GameObject>(path)), "no fix loop while the mesh is missing");

            EditorSceneManager.OpenScene(scenePath);
            AppliedMeshes.RepairPending(); // normally right after the scene opens

            assetCm = AssetWall(path);
            Assert.IsFalse(assetCm.AppliedMeshMissing, "repaired");
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(assetCm.SourceMesh));
            Assert.AreEqual(WallVolume - HoleVolume, SourceMesh.Read(assetCm.GetComponent<MeshFilter>().sharedMesh).Volume(), 1e-3, "the applied hole is back");
            CollectionAssert.DoesNotContain(AppliedMeshes.PendingPrefabs, path);
        }

        [Test]
        public void AppliedMesh_WithoutID_FromAnOlderVersion_IsCutAgainFromTheOriginal()
        {
            BasicCutterSettings.instance.meshStorage = MeshStorage.Embedded;
            var house = MakeHouse(new Vector3(0.5f, 1.6f, 0));
            CutterSystem.SyncNow();
            var cm = house.GetComponentInChildren<CuttableMesh>();
            CutterSystem.ApplyCuts(new[] { cm });
            cm.appliedId = ""; // 1.1.0 data

            string path = TempFolder + "/House.prefab";
            PrefabUtility.SaveAsPrefabAsset(house, path);
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("older version"));
            PrefabMeshFixer.Fix(path);

            var assetCm = AssetWall(path);
            Assert.AreSame(wallAsset, assetCm.SourceMesh);
            Assert.IsFalse(assetCm.HasAppliedCuts);
            Assert.AreEqual(WallVolume - HoleVolume, SourceMesh.Read(assetCm.GetComponent<MeshFilter>().sharedMesh).Volume(), 1e-3, "the window in the prefab cuts it again");
        }
    }
}
