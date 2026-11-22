using System.Collections.Generic;
using BasicCutter.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace BasicCutter.Tests
{
    public class CoreTests
    {
        const float WallW = 4f, WallH = 3f, WallT = 0.3f;

        static SourceMesh BoxWall() => SourceMesh.Read(ProceduralMeshes.Box(new Vector3(WallW, WallH, WallT), Vector3.zero));

        static CutterOutline Outline(List<Vector2> loop, int axis = 2, double depthMin = -0.05, double depthMax = 0.05, double padding = 0, bool door = false)
        {
            var o = new CutterOutline { axis = axis, windowDepthMin = depthMin, windowDepthMax = depthMax };
            var l = new List<D2>();
            foreach (var p in loop) l.Add(new D2(p.x, p.y));
            CutterShapeBuilder.SetLoops(o, new List<List<D2>> { l }, padding, door);
            Assert.IsTrue(o.IsValid, o.error);
            return o;
        }

        static SourceMesh CutAndRead(SourceMesh wall, params CutterOutline[] outlines) => CutAndRead(wall, Matrix4x4.identity, outlines);

        static SourceMesh CutAndRead(SourceMesh wall, Matrix4x4 cutterToWall, params CutterOutline[] outlines) => CutAndRead(wall, cutterToWall, 0, outlines);

        static SourceMesh CutAndRead(SourceMesh wall, Matrix4x4 cutterToWall, double doorSnap, params CutterOutline[] outlines)
        {
            var volumes = new List<List<VolumeTri>>();
            foreach (var o in outlines)
            {
                var place = CutterPlacement.Evaluate(o, cutterToWall, wall, 0.01, doorSnap);
                Assert.IsTrue(place.hit, "cutter should hit the wall");
                volumes.Add(CutterPlacement.VolumeInWallSpace(o, cutterToWall, place.d0, place.d1, place.bottomShift));
            }
            var result = CutEngine.Cut(wall, volumes);
            foreach (bool a in result.cutterAffected) Assert.IsTrue(a, "every cutter should affect the wall");
            var mesh = new Mesh();
            result.WriteTo(mesh, wall);
            return SourceMesh.Read(mesh);
        }

        static void AssertClosed(SourceMesh m)
        {
            Assert.IsTrue(m.IsClosed(out int open, 1e-9), $"result has {open} open edges (bit-exact check)");
        }

        static double PolyArea(List<Vector2> l)
        {
            double a = 0;
            for (int i = 0; i < l.Count; i++) { var p = l[i]; var q = l[(i + 1) % l.Count]; a += p.x * q.y - q.x * p.y; }
            return System.Math.Abs(a) * 0.5;
        }

        [Test]
        public void BoxWall_IsClosed()
        {
            var w = BoxWall();
            AssertClosed(w);
            Assert.AreEqual(WallW * WallH * WallT, w.Volume(), 1e-4);
        }

        [Test]
        public void RectangularWindow_CutsExactHole()
        {
            var r = CutAndRead(BoxWall(), Outline(ProceduralMeshes.Rect(1f, 1.2f, 0.3f, 0.2f)));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 1.0 * 1.2 * WallT, r.Volume(), 1e-4);
            Assert.Less(r.TriangleCount, 60, "fragments should be merged into a clean mesh");
        }

        [Test]
        public void RectangularWindow_HasMinimalTopology()
        {
            // front/back faces: 8 corners + 1 hole => 8 triangles each; 4 jambs as single quads;
            // the 4 untouched box faces keep 2 triangles each
            var r = CutAndRead(BoxWall(), Outline(ProceduralMeshes.Rect(1f, 1.2f, 0.3f, 0.2f)));
            AssertClosed(r);
            Assert.AreEqual(8 + 8 + 4 * 2 + 4 * 2, r.TriangleCount);
        }

        [Test]
        public void Door_DoorMode_JambsAreSingleQuads()
        {
            // front/back U shapes: 8 corners => 6 triangles each; 3 jambs as single quads;
            // top + 2 ends untouched; bottom face split in two rectangles
            var r = CutAndRead(BoxWall(), Matrix4x4.identity, 0.05, Outline(FloatingDoor, padding: -0.01, door: true));
            AssertClosed(r);
            Assert.AreEqual(6 + 6 + 3 * 2 + 3 * 2 + 2 * 2, r.TriangleCount);
        }

        [Test]
        public void ArchWindow_CutsExactHole()
        {
            var arch = ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.0, 1.6, 0.5, 24));
            var r = CutAndRead(BoxWall(), Outline(arch));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - PolyArea(arch) * WallT, r.Volume(), 1e-4);
        }

        [Test]
        public void Door_TouchingWallBottom_StaysClosed()
        {
            // door bottom exactly on the wall's bottom face (coplanar faces)
            var door = ProceduralMeshes.Rect(1f, 2.2f, -1f, -WallH / 2 + 1.1f);
            var r = CutAndRead(BoxWall(), Outline(door));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 1.0 * 2.2 * WallT, r.Volume(), 1e-4);
        }

        // door 1 m wide whose bottom is 1 cm above the wall's bottom (y = -1.5), top at y = 0.7
        static List<Vector2> FloatingDoor => ProceduralMeshes.Rect(1f, 2.19f, 0, -1.49f + 2.19f / 2);

        [Test]
        public void Door_FloatingAboveWallBottom_WithoutDoorMode_LeavesThreshold()
        {
            var r = CutAndRead(BoxWall(), Outline(FloatingDoor));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 1.0 * 2.19 * WallT, r.Volume(), 1e-4);
        }

        [Test]
        public void Door_FloatingAboveWallBottom_DoorMode_CutsToWallBottom()
        {
            var r = CutAndRead(BoxWall(), Matrix4x4.identity, 0.05, Outline(FloatingDoor, door: true));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 1.0 * 2.2 * WallT, r.Volume(), 1e-4, "no 1 cm strip may remain");
        }

        [Test]
        public void Door_DoorMode_NegativePadding_NotAppliedToBottom()
        {
            var door = ProceduralMeshes.Rect(1f, 2.2f, 0, -1.5f + 1.1f); // bottom exactly on the wall bottom
            var r = CutAndRead(BoxWall(), Matrix4x4.identity, 0.05, Outline(door, padding: -0.01, door: true));
            AssertClosed(r);
            // sides and top shrink by 1 cm, the bottom still reaches the wall bottom
            Assert.AreEqual(WallW * WallH * WallT - 0.98 * 2.19 * WallT, r.Volume(), 1e-4);
        }

        [Test]
        public void Door_DoorMode_FarAboveWallBottom_IsNotSnapped()
        {
            // 20 cm above the wall bottom is more than the 5 cm snap: a real parapet, keep it
            var door = ProceduralMeshes.Rect(1f, 2f, 0, -1.3f + 1f);
            var r = CutAndRead(BoxWall(), Matrix4x4.identity, 0.05, Outline(door, door: true));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 1.0 * 2.0 * WallT, r.Volume(), 1e-4);
        }

        /// <summary>Wall (0.3 thick, 3 high) and floor slab (2 x 0.2) as ONE closed mesh, 4 wide.</summary>
        static SourceMesh WallWithFloor()
        {
            // T-shaped profile in (y, z), extruded along X
            var profile = new CutterOutline { axis = 0 };
            var loop = new List<D2>
            {
                new D2(-0.2, -1), new D2(-0.2, 1), new D2(0, 1), new D2(0, 0.15),
                new D2(3, 0.15), new D2(3, -0.15), new D2(0, -0.15), new D2(0, -1),
            };
            CutterShapeBuilder.SetLoops(profile, new List<List<D2>> { loop }, 0);
            var tris = profile.BuildPrism(-2, 2);
            var pos = new List<Vector3>(); var idx = new List<int>();
            foreach (var t in tris) { idx.Add(pos.Count); pos.Add(t.p0.ToVector3()); idx.Add(pos.Count); pos.Add(t.p1.ToVector3()); idx.Add(pos.Count); pos.Add(t.p2.ToVector3()); }
            var m = new Mesh(); m.SetVertices(pos); m.SetTriangles(idx, 0); m.RecalculateNormals(); m.RecalculateBounds();
            var sm = SourceMesh.Read(m);
            Assert.IsTrue(sm.IsClosed(out _));
            return sm;
        }

        // door sunk 2 cm below the floor surface (y = 0), top at 2.2
        static List<Vector2> SunkDoor => ProceduralMeshes.Rect(1f, 2.22f, 0, -0.02f + 2.22f / 2);

        [Test]
        public void Door_InWallWithFloorMesh_WithoutDoorMode_CutsGroove()
        {
            var wall = WallWithFloor();
            var r = CutAndRead(wall, Outline(SunkDoor));
            AssertClosed(r);
            Assert.Greater(wall.Volume() - r.Volume(), 1.0 * 2.2 * 0.3 + 1e-3, "the floor gets a groove");
        }

        [Test]
        public void Door_InWallWithFloorMesh_DoorMode_StopsAtFloorSurface()
        {
            var wall = WallWithFloor();
            var r = CutAndRead(wall, Matrix4x4.identity, 0.05, Outline(SunkDoor, door: true));
            AssertClosed(r);
            Assert.AreEqual(1.0 * 2.2 * 0.3, wall.Volume() - r.Volume(), 1e-4, "only the wall above the floor is removed");
        }

        [Test]
        public void TwoWindows_OnOneWall()
        {
            var a = Outline(ProceduralMeshes.Rect(0.8f, 1f, -1f, 0.3f));
            var b = Outline(ProceduralMeshes.Circle(0.4f, 32, 1f, 0.3f));
            var r = CutAndRead(BoxWall(), a, b);
            AssertClosed(r);
            double expected = WallW * WallH * WallT - (0.8 * 1.0 + PolyArea(ProceduralMeshes.Circle(0.4f, 32))) * WallT;
            Assert.AreEqual(expected, r.Volume(), 1e-4);
        }

        [Test]
        public void ThreeWindows_OnOneWallFace_StayClosed()
        {
            // one flat front face with three holes (needs hole bridging across earlier bridges)
            var wall = SourceMesh.Read(ProceduralMeshes.Box(new Vector3(8.3f, 3, 0.3f), Vector3.zero));
            var rect = ProceduralMeshes.Rect(1.2f, 1.4f, -2.6f, 0.1f);
            var arch = new List<Vector2>();
            foreach (var p in CutterShapeBuilder.ArchOutline(1.2, 1.9, 0.6, 24)) arch.Add(new Vector2((float)p.x, (float)p.y + 0.1f));
            var circle = ProceduralMeshes.Circle(0.5f, 32, 2.6f, 0.3f);
            var r = CutAndRead(wall, Outline(rect), Outline(arch), Outline(circle));
            AssertClosed(r);
            double expected = 8.3 * 3 * 0.3 - (PolyArea(rect) + PolyArea(arch) + PolyArea(circle)) * 0.3;
            Assert.AreEqual(expected, r.Volume(), 1e-4);
        }

        [Test]
        public void Triangulate_ManyHoles_AnyOrientation([Values(0, 45, 90, 180, 270)] int degrees, [Values(false, true)] bool mirror)
        {
            var pts = new List<D2>();
            double ang = degrees * System.Math.PI / 180;
            List<int> Add(List<Vector2> loop, float dx, float dy, bool hole)
            {
                var ids = new List<int>();
                foreach (var p in loop)
                {
                    double x = (mirror ? -1 : 1) * (p.x + dx), y = p.y + dy;
                    ids.Add(pts.Count);
                    pts.Add(new D2(x * System.Math.Cos(ang) - y * System.Math.Sin(ang), x * System.Math.Sin(ang) + y * System.Math.Cos(ang)));
                }
                if (Poly2D.SignedArea(pts, ids) > 0 == hole) ids.Reverse();
                return ids;
            }
            var outer = Add(ProceduralMeshes.Rect(8.3f, 3f), 0, 0, false);
            var holes = new List<List<int>>
            {
                Add(ProceduralMeshes.Rect(1.2f, 1.4f), -2.6f, 0.1f, true),
                Add(ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.2, 1.9, 0.6, 24)), 0, 0.1f, true),
                Add(ProceduralMeshes.Circle(0.5f, 32), 2.6f, 0.3f, true),
                Add(ProceduralMeshes.Rect(0.5f, 0.5f), -1.2f, -0.9f, true),
            };
            double expected = Poly2D.SignedArea(pts, outer);
            foreach (var h in holes) expected += Poly2D.SignedArea(pts, h);
            var tris = Poly2D.Triangulate(pts, outer, holes, 1e-6);
            double area = 0, absArea = 0;
            for (int i = 0; i < tris.Count; i += 3)
            {
                double a = Poly2D.SignedArea(pts, new List<int> { tris[i], tris[i + 1], tris[i + 2] });
                area += a; absArea += System.Math.Abs(a);
            }
            Assert.AreEqual(expected, area, 1e-6);
            Assert.AreEqual(expected, absArea, 1e-6, "no flipped or overlapping triangles");
        }

        [Test]
        public void RotatedAndScaledWall_UsesLocalSpace()
        {
            // cutter rotated 90 degrees around Y relative to a wall whose thin axis is X
            var wall = SourceMesh.Read(ProceduralMeshes.Box(new Vector3(WallT, WallH, WallW), Vector3.zero));
            var cutterToWall = Matrix4x4.TRS(new Vector3(0, 0.2f, 0.5f), Quaternion.Euler(0, 90, 0), Vector3.one);
            var r = CutAndRead(wall, cutterToWall, Outline(ProceduralMeshes.Rect(1f, 1f)));
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - WallT, r.Volume(), 1e-4);
        }

        [Test]
        public void CurvedWall_StaysClosed()
        {
            var wall = SourceMesh.Read(ProceduralMeshes.CurvedWall(4f, 0.3f, 3f, 90f, 32));
            AssertClosed(wall);
            double before = wall.Volume();
            // window on the wall at angle 0 (on the +Z axis), facing along Z
            var cutterToWall = Matrix4x4.Translate(new Vector3(0, 1.5f, 4f));
            var r = CutAndRead(wall, cutterToWall, Outline(ProceduralMeshes.Rect(1f, 1.2f)));
            AssertClosed(r);
            double removed = before - r.Volume();
            Assert.AreEqual(1.0 * 1.2 * 0.3, removed, 0.01);
        }

        [Test]
        public void Floor_BelowDoor_IsNotATarget()
        {
            var floor = SourceMesh.Read(ProceduralMeshes.Box(new Vector3(10, 0.2f, 10), new Vector3(0, -0.1f, 0)));
            // door outline from y = 0 to 2.2 standing on the floor, facing Z
            var door = Outline(ProceduralMeshes.Rect(1f, 2.2f, 0, 1.1f));
            var place = CutterPlacement.Evaluate(door, Matrix4x4.identity, floor, 0.01);
            Assert.IsFalse(place.hit);
        }

        [Test]
        public void WindowInFrontOfWall_IsNotATarget()
        {
            var wall = BoxWall();
            var o = Outline(ProceduralMeshes.Rect(1f, 1f));
            var place = CutterPlacement.Evaluate(o, Matrix4x4.Translate(new Vector3(0, 0, -1.5f)), wall, 0.01);
            Assert.IsFalse(place.hit);
        }

        [Test]
        public void Silhouette_OfFrameWithoutGlass_IsFilledRectangle()
        {
            var frame = SourceMesh.Read(ProceduralMeshes.Frame(ProceduralMeshes.Rect(1.2f, 1.6f), ProceduralMeshes.Rect(1.0f, 1.4f), 0.1f));
            var loops = SilhouetteOf(frame);
            Assert.AreEqual(1, loops.Count);
            Assert.AreEqual(4, loops[0].Count);
            Assert.AreEqual(1.2 * 1.6, Poly2D.SignedArea(loops[0]), 1e-5);
        }

        [Test]
        public void Silhouette_OfOverlappingBars_IsOuterOutline()
        {
            // four overlapping bars forming a frame + a glass panel (open mesh)
            var parts = new List<SourceMesh>
            {
                SourceMesh.Read(ProceduralMeshes.Box(new Vector3(1.2f, 0.1f, 0.1f), new Vector3(0, 0.75f, 0))),
                SourceMesh.Read(ProceduralMeshes.Box(new Vector3(1.2f, 0.1f, 0.1f), new Vector3(0, -0.75f, 0))),
                SourceMesh.Read(ProceduralMeshes.Box(new Vector3(0.1f, 1.6f, 0.1f), new Vector3(0.55f, 0, 0))),
                SourceMesh.Read(ProceduralMeshes.Box(new Vector3(0.1f, 1.6f, 0.1f), new Vector3(-0.55f, 0, 0))),
                SourceMesh.Read(ProceduralMeshes.Panel(ProceduralMeshes.Rect(1.05f, 1.45f), 0)),
            };
            var loops = SilhouetteOf(parts.ToArray());
            Assert.AreEqual(1, loops.Count);
            Assert.AreEqual(4, loops[0].Count);
            Assert.AreEqual(1.2 * 1.6, Poly2D.SignedArea(loops[0]), 1e-5);
        }

        [Test]
        public void Silhouette_OfArchFrame_MatchesArchOutline()
        {
            var outer = ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.2, 1.8, 0.6, 24));
            var inner = ProceduralMeshes.ToVector2(CutterShapeBuilder.ArchOutline(1.0, 1.6, 0.5, 24));
            var frame = SourceMesh.Read(ProceduralMeshes.Frame(outer, inner, 0.1f));
            var loops = SilhouetteOf(frame);
            Assert.AreEqual(1, loops.Count);
            Assert.AreEqual(PolyArea(outer), Poly2D.SignedArea(loops[0]), 1e-5);
        }

        // ------------------------------------------------------------------ local cut (dense walls)

        /// <summary>A closed box whose six faces are regular grids (a "dense" wall like a sculpted model).</summary>
        static Mesh DenseBox(Vector3 size, int nx, int ny, int nz)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            // every vertex comes from the same integer lattice, so shared edges match bit for bit
            Vector3 L(int i, int j, int k) => new Vector3(size.x * ((float)i / nx - 0.5f), size.y * ((float)j / ny - 0.5f), size.z * ((float)k / nz - 0.5f));
            void Face(System.Func<int, int, Vector3> at, int nu, int nv)
            {
                int b0 = verts.Count;
                for (int v = 0; v <= nv; v++)
                    for (int u = 0; u <= nu; u++) verts.Add(at(u, v));
                for (int v = 0; v < nv; v++)
                    for (int u = 0; u < nu; u++)
                    {
                        int a0 = b0 + v * (nu + 1) + u, a1 = a0 + 1, a2 = a0 + nu + 1, a3 = a2 + 1;
                        tris.Add(a0); tris.Add(a2); tris.Add(a1);
                        tris.Add(a1); tris.Add(a2); tris.Add(a3);
                    }
            }
            // (u, v) with u x v pointing inwards; the winding above turns the faces outwards
            Face((u, v) => L(u, v, 0), nx, ny);          // front (-z)
            Face((u, v) => L(nx - u, v, nz), nx, ny);     // back (+z)
            Face((u, v) => L(0, v, nz - u), nz, ny);      // left (-x)
            Face((u, v) => L(nx, v, u), nz, ny);          // right (+x)
            Face((u, v) => L(u, ny, v), nx, nz);          // top (+y)
            Face((u, v) => L(u, 0, nz - v), nx, nz);      // bottom (-y)
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);
            m.RecalculateNormals();
            return m;
        }

        [Test]
        public void DenseWall_LocalCut_MatchesFullCut_AndKeepsTheRestUntouched()
        {
            var wall = SourceMesh.Read(DenseBox(new Vector3(WallW, WallH, WallT), 40, 30, 2));
            AssertClosed(wall);
            Assert.Greater(wall.Volume(), 0, "outward facing");
            Assert.Greater(wall.TriangleCount, 4000);
            var window = Outline(ProceduralMeshes.Rect(1f, 1.2f, 0.33f, 0.21f));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var local = CutAndRead(wall, window);
            long localMs = sw.ElapsedMilliseconds;
            CutEngine.DisableLocalCut = true;
            SourceMesh full;
            sw.Restart();
            try { full = CutAndRead(wall, window); }
            finally { CutEngine.DisableLocalCut = false; }
            Debug.Log($"[Basic Cutter test] dense wall ({wall.TriangleCount} tris): local cut {localMs} ms, whole wall {sw.ElapsedMilliseconds} ms");

            // the local path was really taken (not rejected by its sanity check)
            var place = CutterPlacement.Evaluate(window, Matrix4x4.identity, wall, 0.01);
            var vol = CutterPlacement.VolumeInWallSpace(window, Matrix4x4.identity, place.d0, place.d1);
            Assert.IsFalse(CutEngine.Cut(wall, new List<List<VolumeTri>> { vol }).wholeWall, "local cut was rejected");

            AssertClosed(local);
            AssertClosed(full);
            double expected = WallW * WallH * WallT - 1.0 * 1.2 * WallT;
            Assert.AreEqual(expected, local.Volume(), 1e-4);
            Assert.AreEqual(full.Volume(), local.Volume(), 1e-6);
            // most of the wall is copied as is: the triangle count changes only around the window
            // (re-shaping whole faces would change it by thousands)
            Assert.Less(System.Math.Abs(local.TriangleCount - wall.TriangleCount), 1500);
        }

        [Test]
        public void CurvedWall_LocalCut_MatchesFullCut()
        {
            var wall = SourceMesh.Read(ProceduralMeshes.CurvedWall(5f, 0.3f, 3f, 80f, 48));
            // window facing the wall at angle 0 (the wall is centred on +z at radius 5)
            var toWall = Matrix4x4.TRS(new Vector3(0, 1.5f, 5f), Quaternion.identity, Vector3.one);
            var o = Outline(ProceduralMeshes.Rect(1f, 1.2f));
            var local = CutAndRead(wall, toWall, o);
            CutEngine.DisableLocalCut = true;
            SourceMesh full;
            try { full = CutAndRead(wall, toWall, o); }
            finally { CutEngine.DisableLocalCut = false; }
            AssertClosed(local);
            Assert.AreEqual(full.Volume(), local.Volume(), 1e-6);
        }

        // ------------------------------------------------------------------ niche

        static SourceMesh CutNiche(SourceMesh wall, CutterOutline o, double depth, bool flip, out CutterPlacement.Result place)
        {
            place = CutterPlacement.Evaluate(o, Matrix4x4.identity, wall, 0.01);
            Assert.IsTrue(place.hit);
            CutterPlacement.ApplyNiche(ref place, o, depth, flip);
            var vol = CutterPlacement.VolumeInWallSpace(o, Matrix4x4.identity, place.d0, place.d1, place.bottomShift);
            var result = CutEngine.Cut(wall, new List<List<VolumeTri>> { vol });
            var mesh = new Mesh();
            result.WriteTo(mesh, wall);
            return SourceMesh.Read(mesh);
        }

        [Test]
        public void Niche_CutsOnlyIntoTheNearFace()
        {
            // window model in front of the wall (negative z): the niche opens on that face
            var o = Outline(ProceduralMeshes.Rect(0.8f, 1f), depthMin: -0.2, depthMax: -0.1);
            var r = CutNiche(BoxWall(), o, 0.1, false, out var place);
            Assert.IsTrue(place.niche);
            Assert.IsTrue(place.nicheNear);
            AssertClosed(r);
            Assert.AreEqual(WallW * WallH * WallT - 0.8 * 1.0 * 0.1, r.Volume(), 1e-4);
            // nothing reaches the back face
            foreach (var p in r.positions)
                if (System.Math.Abs(p.x) < 0.39 && System.Math.Abs(p.y) < 0.49) Assert.Less(p.z, WallT / 2 - 0.05);
        }

        [Test]
        public void Niche_Flip_OpensOnTheOtherFace()
        {
            var o = Outline(ProceduralMeshes.Rect(0.8f, 1f), depthMin: -0.2, depthMax: -0.1);
            var r = CutNiche(BoxWall(), o, 0.1, true, out var place);
            Assert.IsFalse(place.nicheNear);
            AssertClosed(r);
            foreach (var p in r.positions)
                if (System.Math.Abs(p.x) < 0.39 && System.Math.Abs(p.y) < 0.49) Assert.Greater(p.z, -WallT / 2 + 0.05);
        }

        [Test]
        public void Niche_DeeperThanWall_GoesThrough()
        {
            var o = Outline(ProceduralMeshes.Rect(0.8f, 1f));
            var r = CutNiche(BoxWall(), o, 1.0, false, out var place);
            Assert.IsTrue(place.nicheTooDeep);
            Assert.IsFalse(place.niche);
            Assert.AreEqual(WallW * WallH * WallT - 0.8 * 1.0 * WallT, r.Volume(), 1e-4);
        }

        // ------------------------------------------------------------------ merging cutters

        [Test]
        public void OverlappingCutters_MergeIntoOneOpening()
        {
            // an L-shaped opening from two overlapping rectangles
            var a = Outline(ProceduralMeshes.Rect(1f, 1f, 0, 0));
            var b = Outline(ProceduralMeshes.Rect(1f, 0.5f, 0.6f, -0.25f));
            var r = CutAndRead(BoxWall(), a, b);
            AssertClosed(r);
            double union = 1.0 + 1.0 * 0.5 - 0.4 * 0.5;
            Assert.AreEqual(WallW * WallH * WallT - union * WallT, r.Volume(), 1e-4);
        }

        [Test]
        public void DoorAndSideLight_SharingEdges_MergeCleanly()
        {
            // both reach the wall bottom (coplanar bottom faces) and overlap by 5 cm
            var door = Outline(ProceduralMeshes.Rect(1f, 2.2f, -0.5f, -1.5f + 1.1f));
            var side = Outline(ProceduralMeshes.Rect(0.5f, 2.2f, 0.2f, -1.5f + 1.1f));
            var r = CutAndRead(BoxWall(), door, side);
            AssertClosed(r);
            double union = (0.45 - (-1.0)) * 2.2;
            Assert.AreEqual(WallW * WallH * WallT - union * WallT, r.Volume(), 1e-4);
        }

        // ------------------------------------------------------------------ jamb material

        [Test]
        public void JambSubmesh_HoldsOnlyTheOpeningsInnerFaces()
        {
            var wall = BoxWall();
            var o = Outline(ProceduralMeshes.Rect(1f, 1.2f));
            var place = CutterPlacement.Evaluate(o, Matrix4x4.identity, wall, 0.01);
            var vol = CutterPlacement.VolumeInWallSpace(o, Matrix4x4.identity, place.d0, place.d1);
            var result = CutEngine.Cut(wall, new List<List<VolumeTri>> { vol }, new[] { 1 });
            Assert.AreEqual(2, result.submeshes.Length);
            Assert.AreEqual(8, result.submeshes[1].Count / 3, "4 jamb quads");
            for (int i = 0; i < result.submeshes[1].Count; i++)
                Assert.AreEqual(0, result.normals[result.submeshes[1][i]].z, 1e-4, "jamb faces are perpendicular to the wall");
            var mesh = new Mesh();
            result.WriteTo(mesh, wall);
            AssertClosed(SourceMesh.Read(mesh));
        }

        // ------------------------------------------------------------------ trim

        static Bounds TrimBounds(TrimMeshData d)
        {
            var b = new Bounds(d.positions[0], Vector3.zero);
            foreach (var p in d.positions) b.Encapsulate(p);
            return b;
        }

        [Test]
        public void Trim_LiesOnTheWallSurface()
        {
            var wall = BoxWall();
            var o = Outline(ProceduralMeshes.Rect(1f, 1.2f));
            var place = CutterPlacement.Evaluate(o, Matrix4x4.identity, wall, 0.01);
            var s = new TrimSettings { nearSide = true, farSide = false, width = 0.1, thickness = 0.03 };
            var d = TrimBuilder.Build(o, place, Matrix4x4.identity, wall, s);
            Assert.IsFalse(d.IsEmpty);
            var b = TrimBounds(d);
            Assert.AreEqual(-WallT / 2 - 0.03, b.min.z, 1e-4, "stands 3 cm out of the near face");
            Assert.Less(b.max.z, -WallT / 2 + 0.003, "reaches only slightly into the wall");
            Assert.AreEqual(1.2, b.size.x, 1e-4);
            Assert.AreEqual(1.4, b.size.y, 1e-4);
        }

        [Test]
        public void Trim_BothSides_AndSill()
        {
            var wall = BoxWall();
            var o = Outline(ProceduralMeshes.Rect(1f, 1.2f));
            var place = CutterPlacement.Evaluate(o, Matrix4x4.identity, wall, 0.01);
            var s = new TrimSettings { nearSide = true, farSide = true, width = 0.1, thickness = 0.03, sill = true, sillDepth = 0.05, sillOverhang = 0.04 };
            var b = TrimBounds(TrimBuilder.Build(o, place, Matrix4x4.identity, wall, s));
            Assert.AreEqual(-WallT / 2 - 0.08, b.min.z, 1e-4, "the sill stands out further than the trim");
            Assert.AreEqual(WallT / 2 + 0.08, b.max.z, 1e-4);
            Assert.AreEqual(1.2 + 0.08, b.size.x, 1e-4, "the sill overhangs the trim");
        }

        [Test]
        public void Trim_Door_HasNoBottomPiece()
        {
            var wall = BoxWall();
            var o = Outline(ProceduralMeshes.Rect(1f, 2.2f, 0, -1.5f + 1.1f), door: true);
            var place = CutterPlacement.Evaluate(o, Matrix4x4.identity, wall, 0.01, 0.05);
            var s = new TrimSettings { nearSide = true, farSide = false, width = 0.1, thickness = 0.03, sill = true };
            var b = TrimBounds(TrimBuilder.Build(o, place, Matrix4x4.identity, wall, s));
            Assert.AreEqual(-1.5 + place.bottomShift, b.min.y, 1e-4, "starts at the opening's bottom, no piece and no sill below");
            Assert.AreEqual(1.2, b.size.x, 1e-4, "square ends: no sideways miter at the floor");
        }

        [Test]
        public void Trim_OnCurvedWall_FollowsTheSurface()
        {
            var wall = SourceMesh.Read(ProceduralMeshes.CurvedWall(5f, 0.3f, 3f, 80f, 48));
            var toWall = Matrix4x4.TRS(new Vector3(0, 1.5f, 5f), Quaternion.identity, Vector3.one);
            var o = Outline(ProceduralMeshes.Rect(1.2f, 1.2f));
            var place = CutterPlacement.Evaluate(o, toWall, wall, 0.01);
            var s = new TrimSettings { nearSide = true, farSide = true, width = 0.1, thickness = 0.03 };
            var d = TrimBuilder.Build(o, place, toWall, wall, s);
            Assert.IsFalse(d.IsEmpty);
            // every trim vertex is within the trim thickness of a wall surface (radius 4.85 or 5.15)
            foreach (var v in d.positions)
            {
                var w = toWall.MultiplyPoint3x4(v);
                double r = System.Math.Sqrt(w.x * w.x + w.z * w.z);
                double gap = System.Math.Min(System.Math.Abs(r - 4.85), System.Math.Abs(r - 5.15));
                Assert.Less(gap, 0.035, $"vertex at radius {r:0.####}");
            }
        }

        static List<List<D2>> SilhouetteOf(params SourceMesh[] meshes)
        {
            var pts = new List<D2>();
            var tris = new List<int>();
            foreach (var m in meshes)
            {
                int b = pts.Count;
                foreach (var p in m.positions) pts.Add(new D2(p.x, p.y));
                foreach (int i in m.triangles) tris.Add(b + i);
            }
            return Silhouette.Build(pts, tris);
        }
    }
}
