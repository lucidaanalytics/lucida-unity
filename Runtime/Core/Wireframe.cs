using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lucida
{
    public enum WireKind { Container, Text, Input, Image, Button }

    /// <summary>
    /// Engine-neutral UI node (the Unity adapter converts a RectTransform hierarchy into this).
    /// Coordinates are screen points with a top-left origin, relative to the parent's origin.
    /// </summary>
    public sealed class WireNode
    {
        public WireKind Kind;
        public float X, Y, Width, Height;
        public string Text;
        /// <summary>GameObject or component name, exposed as the element class (<c>lc-&lt;name&gt;</c>).</summary>
        public string Name;
        /// <summary>The app opted this node out of masking (LucidaReplayUnmask). Never honoured for passwords.</summary>
        public bool Unmask;
        /// <summary>Replaced by an empty placeholder box; its subtree is never serialised.</summary>
        public bool Block;
        /// <summary>Password-like input: its value is always starred.</summary>
        public bool Password;
        public List<WireNode> Children = new List<WireNode>();
    }

    /// <summary>One capture of the current screen.</summary>
    public sealed class WireSnapshot
    {
        public WireNode Root;
        public string Screen = "Unknown";
        public int Width, Height;
    }

    /// <summary>Masking switches; all on by default (privacy by default).</summary>
    public sealed class WireMask
    {
        public bool Text = true;
        public bool Inputs = true;
        public bool Images = true;
    }

    /// <summary>
    /// UI hierarchy -> mobile replay events in the rrweb shape the iOS/Android/Flutter SDKs emit and
    /// crates/lc-replay decodes: Meta (type 4, <c>href</c> = <c>app://&lt;screen&gt;</c>), a <c>navigation</c>
    /// custom event (type 5), FullSnapshot (type 2: document -> html -> head/body -> absolutely positioned
    /// elements; body id 5, elements from 6) and taps as MouseInteraction Click (type 3, source 2, type 2).
    /// Text is starred with whitespace kept; inputs are starred; blocked nodes carry <c>data-lc-block</c>.
    /// </summary>
    public sealed class WireframeFrame
    {
        public const int BodyId = 5;
        public const string HrefPrefix = "app://";

        public readonly List<Dictionary<string, object>> Events;
        readonly List<float[]> boxes; // id, left, top, right, bottom (absolute)

        WireframeFrame(List<Dictionary<string, object>> events, List<float[]> boxes)
        {
            Events = events;
            this.boxes = boxes;
        }

        /// <summary>Every non-whitespace character becomes <c>*</c>: layout survives, content does not.</summary>
        public static string Stars(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(char.IsWhiteSpace(ch) ? ch : '*');
            return sb.ToString();
        }

        static string Px(float v)
        {
            return v == (float)System.Math.Round(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        static Dictionary<string, object> El(string tag, int id, Dictionary<string, object> attrs, List<object> kids)
        {
            return new Dictionary<string, object>
            {
                { "type", 2 }, { "tagName", tag }, { "attributes", attrs }, { "childNodes", kids }, { "id", id },
            };
        }

        static Dictionary<string, object> Event(int type, long ts, Dictionary<string, object> data)
        {
            return new Dictionary<string, object> { { "type", type }, { "timestamp", ts }, { "data", data } };
        }

        public static Dictionary<string, object> Meta(WireSnapshot s, long ts)
        {
            return Event(4, ts, new Dictionary<string, object> { { "href", HrefPrefix + s.Screen }, { "width", s.Width }, { "height", s.Height } });
        }

        public static Dictionary<string, object> Navigation(WireSnapshot s, long ts)
        {
            return Event(5, ts, new Dictionary<string, object>
            {
                { "tag", "navigation" }, { "payload", new Dictionary<string, object> { { "url", HrefPrefix + s.Screen } } },
            });
        }

        /// <summary>The FullSnapshot of <paramref name="s"/> plus a hit-test over the serialised node ids.</summary>
        public static WireframeFrame Build(WireSnapshot s, long ts, WireMask mask)
        {
            var next = 6;
            var boxes = new List<float[]>();

            Dictionary<string, object> Node(WireNode v, float ox, float oy)
            {
                var id = next++;
                float left = ox + v.X, top = oy + v.Y;
                boxes.Add(new float[] { id, left, top, left + v.Width, top + v.Height });
                var tag = v.Block ? "div" : v.Kind == WireKind.Text ? "span" : v.Kind == WireKind.Input ? "input" : v.Kind == WireKind.Button ? "button" : "div";
                var attrs = new Dictionary<string, object>
                {
                    { "style", "position:absolute;left:" + Px(v.X) + "px;top:" + Px(v.Y) + "px;width:" + Px(v.Width) + "px;height:" + Px(v.Height) + "px;box-sizing:border-box;border:1px solid #bbb" },
                    { "class", "lc-" + (v.Name ?? v.Kind.ToString().ToLowerInvariant()) },
                };
                var kids = new List<object>();
                if (v.Block)
                {
                    attrs["data-lc-block"] = "";
                }
                else if (v.Kind == WireKind.Input)
                {
                    var masked = v.Password || (mask.Inputs && !v.Unmask);
                    attrs["type"] = v.Password ? "password" : "text";
                    attrs["value"] = masked ? Stars(v.Text ?? "") : v.Text ?? "";
                    if (masked) attrs["data-lc-mask"] = "";
                }
                else
                {
                    var masked = mask.Text && !v.Unmask;
                    if (!string.IsNullOrEmpty(v.Text))
                    {
                        if (masked) attrs["data-lc-mask"] = "";
                        kids.Add(new Dictionary<string, object> { { "type", 3 }, { "textContent", masked ? Stars(v.Text) : v.Text }, { "id", next++ } });
                    }
                    if (v.Kind == WireKind.Image)
                    {
                        attrs["data-lc-image"] = ""; // the wireframe never holds pixels either way
                        if (mask.Images && !v.Unmask) attrs["data-lc-mask"] = "";
                    }
                    foreach (var c in v.Children) kids.Add(Node(c, left, top));
                }
                return El(tag, id, attrs, kids);
            }

            var content = Node(s.Root, 0, 0);
            var body = El("body", BodyId, new Dictionary<string, object> { { "style", "position:relative;margin:0;width:" + s.Width + "px;height:" + s.Height + "px" } }, new List<object> { content });
            var head = El("head", 4, new Dictionary<string, object>(), new List<object>());
            var html = El("html", 3, new Dictionary<string, object>(), new List<object> { head, body });
            var doctype = new Dictionary<string, object> { { "type", 1 }, { "name", "html" }, { "publicId", "" }, { "systemId", "" }, { "id", 2 } };
            var doc = new Dictionary<string, object> { { "type", 0 }, { "id", 1 }, { "childNodes", new List<object> { doctype, html } } };
            var full = Event(2, ts, new Dictionary<string, object>
            {
                { "node", doc }, { "initialOffset", new Dictionary<string, object> { { "left", 0 }, { "top", 0 } } },
            });
            return new WireframeFrame(new List<Dictionary<string, object>> { full }, boxes);
        }

        /// <summary>Id of the deepest serialised node containing the absolute point, or the body.</summary>
        public int NodeAt(float x, float y)
        {
            for (var i = boxes.Count - 1; i >= 0; i--)
            {
                var b = boxes[i];
                if (x >= b[1] && x < b[3] && y >= b[2] && y < b[4]) return (int)b[0];
            }
            return BodyId;
        }

        /// <summary>A tap as an rrweb MouseInteraction Click.</summary>
        public Dictionary<string, object> Tap(float x, float y, long ts)
        {
            return Event(3, ts, new Dictionary<string, object> { { "source", 2 }, { "type", 2 }, { "id", NodeAt(x, y) }, { "x", x }, { "y", y } });
        }
    }
}
