using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BasicCutter.EditorTools;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace BasicCutter.Tests
{
    /// <summary>
    /// Opens the demo scene in a normal (non batch) editor, shows the inspector of a few cutters in a
    /// floating window and saves each as a PNG in Logs/. Run with -executeMethod; the editor quits itself.
    /// </summary>
    public static class InspectorSnapshot
    {
        static readonly (string obj, string file, Action prepare)[] Shots =
        {
            ("Door", "inspector_door.png", null),
            ("Rect Window (Wall B)", "inspector_window_trim.png", null),
            ("Niche", "inspector_niche_advanced.png", () => EditorPrefs.SetBool("BasicCutter.Inspector.Advanced", true)),
        };

        static EditorWindow window;
        static int index = -1;
        static int phase;
        static double next;

        public static void Run()
        {
            Directory.CreateDirectory("Logs");
            foreach (var key in new[] { "Shape", "Door", "Niche", "Surfaces" }) EditorPrefs.SetBool("BasicCutter.Inspector." + key, true);
            EditorPrefs.SetBool("BasicCutter.Inspector.Advanced", false);
            DemoScene.Build(DemoScene.DefaultFolder);
            NextShot();
            EditorApplication.update += Tick;
        }

        static void NextShot()
        {
            if (window != null) window.Close();
            index++;
            if (index >= Shots.Length) { EditorApplication.Exit(0); return; }
            Shots[index].prepare?.Invoke();
            Selection.activeGameObject = GameObject.Find(Shots[index].obj);
            // a fresh inspector shows the current selection
            var type = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
            window = (EditorWindow)ScriptableObject.CreateInstance(type);
            window.ShowUtility();
            window.position = new Rect(80, 80, 440, 1000);
            phase = 0;
            next = EditorApplication.timeSinceStartup + 2.5;
        }

        static void Tick()
        {
            if (window == null) return;
            window.Repaint();
            if (EditorApplication.timeSinceStartup < next) return;
            // the first grab can return an older frame: grab twice and keep the second
            try { Capture(window, Path.Combine("Logs", Shots[index].file)); }
            catch (Exception e) { Debug.LogException(e); }
            if (++phase < 2) { next = EditorApplication.timeSinceStartup + 1.0; return; }
            NextShot();
        }

        static void Capture(EditorWindow w, string path)
        {
            float ppp = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.RoundToInt(w.position.width * ppp), height = Mathf.RoundToInt(w.position.height * ppp);
            Texture2D tex = null;

            // preferred: read the window's own back buffer (not affected by other windows on top)
            var parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(w);
            var grab = parent?.GetType().GetMethod("GrabPixels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (grab != null)
            {
                var rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                grab.Invoke(parent, new object[] { rt, new Rect(0, 0, width, height) });
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                // the view's buffer is upside down
                var px = tex.GetPixels32();
                var flipped = new Color32[px.Length];
                for (int y = 0; y < height; y++) Array.Copy(px, y * width, flipped, (height - 1 - y) * width, width);
                tex.SetPixels32(flipped);
                tex.Apply();
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(rt);
                Debug.Log($"[Basic Cutter snapshot] grabbed {path} via GrabPixels ({width}x{height})");
            }
            else
            {
                var pixels = InternalEditorUtility.ReadScreenPixel(w.position.position, (int)w.position.width, (int)w.position.height);
                tex = new Texture2D((int)w.position.width, (int)w.position.height, TextureFormat.RGB24, false);
                tex.SetPixels(pixels);
                tex.Apply();
                Debug.Log($"[Basic Cutter snapshot] grabbed {path} via ReadScreenPixel");
            }
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }
}
