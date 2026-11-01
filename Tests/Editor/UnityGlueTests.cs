using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Lucida.Tests.Editor
{
    /// <summary>EditMode tests of the engine glue (Runtime/Unity and Editor): needs the Unity Test Runner.</summary>
    public class UnityGlueTests
    {
        readonly List<GameObject> made = new List<GameObject>();

        GameObject Make(string name, params System.Type[] components)
        {
            var go = new GameObject(name, components);
            made.Add(go);
            return go;
        }

        [TearDown]
        public void CleanUp()
        {
            foreach (var go in made) if (go != null) Object.DestroyImmediate(go);
            made.Clear();
        }

        Camera MainCamera()
        {
            var cam = Make("Main Camera", typeof(Camera)).GetComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = Vector3.zero;
            cam.transform.rotation = Quaternion.identity; // looks down +z
            return cam;
        }

        GameObject Cube(string name, Vector3 at)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = at;
            made.Add(go);
            return go;
        }

        static IEnumerable<WireNode> All(WireNode n)
        {
            yield return n;
            foreach (var c in n.Children) foreach (var d in All(c)) yield return d;
        }

        [Test]
        public void UiTextIsCapturedAndMaskedOnTheWire()
        {
            var canvas = Make("Canvas", typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var label = Make("Label", typeof(RectTransform), typeof(Text));
            label.transform.SetParent(canvas.transform, false);
            label.GetComponent<Text>().text = "secret code";
            var snap = new UnityWireframeSource(false).Capture();
            Assert.IsTrue(All(snap.Root).Any(n => n.Kind == WireKind.Text && n.Text == "secret code"));
            var json = Json.Serialize(WireframeFrame.Build(snap, 1, new WireMask()).Events);
            StringAssert.DoesNotContain("secret", json);
        }

        [Test]
        public void CubeInFrontProjectsAndCubeBehindDoesNot()
        {
            var cam = MainCamera();
            if (cam.pixelWidth <= 0 || cam.pixelHeight <= 0) Assert.Ignore("no render target size in this batch mode");
            var front = Cube("front", new Vector3(0, 0, 5)).GetComponent<Renderer>();
            var behind = Cube("behind", new Vector3(0, 0, -5)).GetComponent<Renderer>();
            var r = UnitySceneWireframe.Project(cam, front.bounds, cam.pixelWidth, cam.pixelHeight);
            Assert.IsNotNull(r);
            Assert.Greater(r[2], 0);
            Assert.Greater(r[3], 0);
            Assert.IsNull(UnitySceneWireframe.Project(cam, behind.bounds, cam.pixelWidth, cam.pixelHeight));
        }

        [Test]
        public void SceneNodesHonourBlockAndMaskText()
        {
            var cam = MainCamera();
            if (cam.pixelWidth <= 0 || cam.pixelHeight <= 0) Assert.Ignore("no render target size in this batch mode");
            Cube("vault", new Vector3(1, 0, 6)).AddComponent<LucidaReplayBlock>();
            var sign = Make("sign", typeof(MeshRenderer), typeof(TextMesh));
            sign.transform.position = new Vector3(-1, 0, 4);
            sign.GetComponent<TextMesh>().text = "Alice";
            var nodes = UnitySceneWireframe.Capture(cam, cam.pixelWidth, cam.pixelHeight, 50);
            Assert.IsTrue(nodes.Any(n => n.Name == "vault" && n.Block));
            var root = new WireNode { Kind = WireKind.Container, Width = cam.pixelWidth, Height = cam.pixelHeight };
            root.Children.AddRange(nodes);
            var json = Json.Serialize(WireframeFrame.Build(new WireSnapshot { Root = root, Width = cam.pixelWidth, Height = cam.pixelHeight }, 1, new WireMask()).Events);
            StringAssert.DoesNotContain("Alice", json);
        }

        [Test]
        public void SettingsRefuseSecretKeysAndMapToOptions()
        {
            var s = ScriptableObject.CreateInstance<LucidaSettings>();
            s.ApiKey = "lc_sk_nope";
            Assert.IsNotNull(s.Validate());
            s.ApiKey = "lc_pk_ok";
            s.SessionReplay = true;
            s.CaptureScene = false;
            Assert.IsNull(s.Validate());
            var o = s.ToOptions();
            Assert.AreEqual("lc_pk_ok", o.ApiKey);
            Assert.IsTrue(o.SessionReplay);
            Assert.IsFalse(o.Replay.CaptureScene);
            Object.DestroyImmediate(s);
        }

        [Test]
        public void SettingsProviderCreatesTheAssetOnce()
        {
            var existed = AssetDatabase.LoadAssetAtPath<LucidaSettings>(global::Lucida.Editor.LucidaSettingsProvider.AssetPath) != null;
            var a = global::Lucida.Editor.LucidaSettingsProvider.GetOrCreate();
            Assert.AreSame(a, global::Lucida.Editor.LucidaSettingsProvider.GetOrCreate());
            Assert.IsNotNull(global::Lucida.Editor.LucidaSettingsProvider.Create());
            if (!existed) AssetDatabase.DeleteAsset(global::Lucida.Editor.LucidaSettingsProvider.AssetPath);
        }
    }
}
