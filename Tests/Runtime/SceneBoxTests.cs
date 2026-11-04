using System.Collections.Generic;
using NUnit.Framework;

namespace Lucida.Tests
{
    /// <summary>The 3D scene projection maths (engine-free, so it also runs under dotnet test).</summary>
    public class SceneBoxTests
    {
        static List<float[]> Box(float x0, float y0, float x1, float y1, float z)
        {
            return new List<float[]> { new[] { x0, y0, z }, new[] { x1, y0, z }, new[] { x0, y1, z }, new[] { x1, y1, z + 2 } };
        }

        [Test]
        public void ProjectsToTopLeftRect()
        {
            Assert.AreEqual(new[] { 100f, 100f, 200f, 300f, 5f }, SceneBox.Rect(Box(100, 200, 300, 500, 5), 800, 600));
        }

        [Test]
        public void ClipsToScreenAndDropsOffscreenOrBehind()
        {
            Assert.AreEqual(new[] { 0f, 0f, 50f, 600f, 1f }, SceneBox.Rect(Box(-100, -10, 50, 700, 1), 800, 600));
            Assert.IsNull(SceneBox.Rect(Box(900, 10, 1000, 20, 1), 800, 600));
            Assert.IsNull(SceneBox.Rect(Box(10, 10, 20, 20, -3), 800, 600));
        }

        static float[] P(float x, float y, float z) { return new[] { 400 + 500 * x / z, 300 + 500 * y / z, z }; }

        static List<float[]> Cube(float x0, float x1, float y0, float y1, float zFront, float zBack)
        {
            var l = new List<float[]>();
            for (var i = 0; i < 8; i++) l.Add(P((i & 1) == 0 ? x0 : x1, (i & 2) == 0 ? y0 : y1, (i & 4) == 0 ? zFront : zBack));
            return l;
        }

        [Test]
        public void BoxCrossingTheNearPlaneIsClippedThere()
        {
            // spans the camera: the old front-corners-only size would have been 500x500 in the middle
            Assert.AreEqual(new[] { 0f, 0f, 800f, 600f, SceneBox.NearPlane }, SceneBox.Rect(Cube(-1, 1, -1, 1, 2, -2), 800, 600));
            // x from 0 to 1 in front of the left edge of the view: the right side runs off the screen at the near plane
            var r = SceneBox.Rect(Cube(0, 1, 0, 1, 5, -1), 800, 600);
            Assert.AreEqual(400f, r[0], 0.5f);
            Assert.AreEqual(400f, r[2], 0.5f);
            // wholly behind: nothing
            Assert.IsNull(SceneBox.Rect(Cube(0, 1, 0, 1, -1, -3), 800, 600));
        }

        [Test]
        public void SortsFarthestFirst()
        {
            var l = new List<KeyValuePair<float, WireNode>>
            {
                new KeyValuePair<float, WireNode>(1, new WireNode { Name = "near" }),
                new KeyValuePair<float, WireNode>(9, new WireNode { Name = "far" }),
            };
            SceneBox.SortFarFirst(l);
            Assert.AreEqual("far", l[0].Value.Name);
        }

        [Test]
        public void SceneTextIsMaskedByDefault()
        {
            var root = new WireNode { Kind = WireKind.Container, Width = 100, Height = 100 };
            root.Children.Add(new WireNode { Kind = WireKind.Text, Width = 50, Height = 10, Text = "Player Alice", Name = "scene-label" });
            var json = Json.Serialize(WireframeFrame.Build(new WireSnapshot { Root = root, Width = 100, Height = 100 }, 1, new WireMask()).Events);
            StringAssert.DoesNotContain("Alice", json);
            StringAssert.Contains("****** *****", json);
        }
    }
}
