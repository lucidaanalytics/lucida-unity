#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using UnityEngine;

namespace Lucida
{
    /// <summary>
    /// 3D half of the Unity wireframe: every enabled, visible Renderer is projected through the camera
    /// (its world bounds' 8 corners) to a screen box. Meshes become empty named boxes (no pixels, no textures);
    /// a TextMesh or 3D TextMeshPro becomes a text node, starred by the default mask. <see cref="LucidaReplayBlock"/>
    /// blanks an object and <see cref="LucidaReplayUnmask"/> unmasks its text, as in uGUI.
    /// </summary>
    public static class UnitySceneWireframe
    {
        static readonly float[][] Corners = { new float[3], new float[3], new float[3], new float[3], new float[3], new float[3], new float[3], new float[3] };

        /// <summary>Scene nodes in paint order (farthest first), top-left screen coordinates, at most <paramref name="max"/>.</summary>
        public static List<WireNode> Capture(Camera cam, int screenW, int screenH, int max)
        {
            var byDepth = new List<KeyValuePair<float, WireNode>>();
            if (cam == null || !cam.isActiveAndEnabled || max <= 0) return new List<WireNode>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || r is ParticleSystemRenderer) continue;
                if ((cam.cullingMask & (1 << r.gameObject.layer)) == 0) continue;
                var rect = Project(cam, r.bounds, screenW, screenH);
                if (rect == null) continue;
                var go = r.gameObject;
                var n = new WireNode
                {
                    Kind = WireKind.Container, X = rect[0], Y = rect[1], Width = rect[2], Height = rect[3], Name = go.name,
                    Block = go.GetComponentInParent<LucidaReplayBlock>() != null,
                    Unmask = go.GetComponentInParent<LucidaReplayUnmask>() != null,
                };
                if (!n.Block) ReadText(go, n);
                byDepth.Add(new KeyValuePair<float, WireNode>(rect[4], n));
            }
            byDepth.Sort((a, b) => a.Key.CompareTo(b.Key)); // nearest first, to keep the nearest when capped
            if (byDepth.Count > max) byDepth.RemoveRange(max, byDepth.Count - max);
            SceneBox.SortFarFirst(byDepth);
            var nodes = new List<WireNode>(byDepth.Count);
            foreach (var kv in byDepth) nodes.Add(kv.Value);
            return nodes;
        }

        /// <summary>{left, top, width, height, depth} of <paramref name="b"/> on screen, or null when not visible.</summary>
        public static float[] Project(Camera cam, Bounds b, int screenW, int screenH)
        {
            Vector3 mn = b.min, mx = b.max;
            for (var i = 0; i < 8; i++)
            {
                var p = cam.WorldToScreenPoint(new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z));
                Corners[i][0] = p.x;
                Corners[i][1] = p.y;
                Corners[i][2] = p.z;
            }
            return SceneBox.Rect(Corners, screenW, screenH);
        }

        static void ReadText(GameObject go, WireNode n)
        {
            var tm = go.GetComponent<TextMesh>();
            if (tm != null) { n.Kind = WireKind.Text; n.Text = tm.text; return; }
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "TextMeshPro") continue; // 3D TMP, read by reflection (no TMP dependency)
                var p = c.GetType().GetProperty("text");
                n.Kind = WireKind.Text;
                n.Text = p == null ? null : p.GetValue(c, null) as string;
                return;
            }
        }
    }
}
#endif
