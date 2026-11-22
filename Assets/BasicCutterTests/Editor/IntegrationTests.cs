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
    }
}
