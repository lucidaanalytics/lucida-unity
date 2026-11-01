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
