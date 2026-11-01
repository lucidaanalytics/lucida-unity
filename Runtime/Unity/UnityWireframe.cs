#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Lucida
{
    /// <summary>Add to a GameObject to send its text unmasked in replay. Never unmasks password fields.</summary>
    public sealed class LucidaReplayUnmask : MonoBehaviour { }

    /// <summary>Add to a GameObject to replace it (and its children) with an empty box in replay.</summary>
    public sealed class LucidaReplayBlock : MonoBehaviour { }

    /// <summary>
    /// <see cref="IWireframeSource"/> over uGUI: every active root Canvas, walked through its RectTransforms.
    /// TextMeshPro is read by type name and reflection so the package needs no TMP dependency.
    /// The screen name is the active scene's name.
    /// </summary>
    public sealed class UnityWireframeSource : IWireframeSource
    {
        readonly Vector3[] corners = new Vector3[4];

        public WireSnapshot Capture()
        {
            int w = UnityEngine.Screen.width, h = UnityEngine.Screen.height;
            var root = new WireNode { Kind = WireKind.Container, Width = w, Height = h, Name = "screen" };
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!canvas.isRootCanvas || !canvas.enabled || !canvas.gameObject.activeInHierarchy) continue;
                var cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
                root.Children.Add(Walk((RectTransform)canvas.transform, cam, h, 0, 0));
            }
            return new WireSnapshot { Root = root, Screen = SceneManager.GetActiveScene().name, Width = w, Height = h };
        }

        WireNode Walk(RectTransform t, Camera cam, int screenH, float parentX, float parentY)
        {
            t.GetWorldCorners(corners);
            var bl = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            var tr = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            float x = bl.x, y = screenH - tr.y; // Unity screen space is bottom-left; the wireframe is top-left
            var n = new WireNode
            {
                X = x - parentX, Y = y - parentY, Width = tr.x - bl.x, Height = tr.y - bl.y, Name = t.name,
                Unmask = t.GetComponent<LucidaReplayUnmask>() != null, Block = t.GetComponent<LucidaReplayBlock>() != null,
            };
            if (n.Block) return n;
            Classify(t.gameObject, n);
            if (n.Kind == WireKind.Input) return n; // an input's children are its placeholder and text: the value covers them
            foreach (Transform c in t)
                if (c.gameObject.activeInHierarchy && c is RectTransform rc) n.Children.Add(Walk(rc, cam, screenH, x, y));
            return n;
        }

        static void Classify(GameObject go, WireNode n)
        {
            var input = go.GetComponent<InputField>();
            if (input != null)
            {
                n.Kind = WireKind.Input;
                n.Text = input.text;
                n.Password = input.contentType == InputField.ContentType.Password || input.inputType == InputField.InputType.Password;
                return;
            }
            foreach (var c in go.GetComponents<Component>())
            {
                var type = c == null ? "" : c.GetType().Name;
                if (type == "TMP_InputField")
                {
                    n.Kind = WireKind.Input;
                    n.Text = Prop(c, "text") as string;
                    n.Password = (Prop(c, "contentType") ?? "").ToString() == "Password";
                    return;
                }
                if (type == "TextMeshProUGUI")
                {
                    n.Kind = WireKind.Text;
                    n.Text = Prop(c, "text") as string;
                    return;
                }
            }
            var text = go.GetComponent<Text>();
            if (text != null) { n.Kind = WireKind.Text; n.Text = text.text; return; }
            if (go.GetComponent<Button>() != null) { n.Kind = WireKind.Button; return; }
            if (go.GetComponent<Image>() != null || go.GetComponent<RawImage>() != null) { n.Kind = WireKind.Image; return; }
            n.Kind = WireKind.Container;
        }

        static object Prop(Component c, string name)
        {
            var p = c.GetType().GetProperty(name);
            return p == null ? null : p.GetValue(c, null);
        }
    }
}
#endif
