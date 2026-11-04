using System.Collections.Generic;

namespace Lucida
{
    /// <summary>
    /// Engine-free half of the 3D scene wireframe: turns the projected corners of an object's bounds into a screen
    /// rectangle. The Unity adapter projects the 8 corners with <c>Camera.WorldToScreenPoint</c> and calls this.
    /// </summary>
    public static class SceneBox
    {
        /// <summary>Distance in front of the camera where a box that reaches behind it is cut.</summary>
        public const float NearPlane = 0.01f;

        /// <summary>
        /// <paramref name="corners"/> are {x, y, z}: screen pixels with a bottom-left origin and z the distance in
        /// front of the camera. Returns {left, top, width, height, nearest z} with a top-left origin, clipped to the
        /// screen, or null when the box is behind the camera or off screen.
        /// A full box (8 corners, bit 0/1/2 = max x/y/z, as <c>UnitySceneWireframe</c> projects them) that crosses the
        /// camera's near plane is clipped there: each edge from a front corner to a back corner contributes the point
        /// where it meets z = <see cref="NearPlane"/>, found in camera space (x and y scale with z, around the screen
        /// centre). Fewer corners are used as given, those behind the camera dropped.
        /// </summary>
        public static float[] Rect(IList<float[]> corners, int screenW, int screenH)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, near = float.MaxValue;
            var any = false;
            var points = new List<float[]>(corners.Count + 4);
            foreach (var c in corners) if (c[2] > 0) points.Add(c);
            if (corners.Count == 8)
            {
                for (var i = 0; i < 8; i++)
                    for (var bit = 1; bit <= 4; bit <<= 1)
                    {
                        var j = i | bit;
                        if (j == i) continue;
                        var a = corners[i];
                        var b = corners[j];
                        if ((a[2] > 0) == (b[2] > 0)) continue;
                        var f = (a[2] - NearPlane) / (a[2] - b[2]);
                        const float z = NearPlane; // by construction of f
                        // x and y are cx + k/z, so (x - cx) * z is linear along the edge
                        float cx = screenW / 2f, cy = screenH / 2f;
                        var u = (a[0] - cx) * a[2] + ((b[0] - cx) * b[2] - (a[0] - cx) * a[2]) * f;
                        var v = (a[1] - cy) * a[2] + ((b[1] - cy) * b[2] - (a[1] - cy) * a[2]) * f;
                        points.Add(new[] { cx + u / z, cy + v / z, z });
                    }
            }
            foreach (var c in points)
            {
                any = true;
                if (c[0] < minX) minX = c[0];
                if (c[0] > maxX) maxX = c[0];
                if (c[1] < minY) minY = c[1];
                if (c[1] > maxY) maxY = c[1];
                if (c[2] < near) near = c[2];
            }
            if (!any) return null;
            if (minX < 0) minX = 0;
            if (minY < 0) minY = 0;
            if (maxX > screenW) maxX = screenW;
            if (maxY > screenH) maxY = screenH;
            if (maxX <= minX || maxY <= minY) return null;
            return new[] { minX, screenH - maxY, maxX - minX, maxY - minY, near };
        }

        /// <summary>Paint order for scene nodes: farthest first, so nearer objects end up on top in the replay.</summary>
        public static void SortFarFirst(List<KeyValuePair<float, WireNode>> byDepth)
        {
            byDepth.Sort((a, b) => b.Key.CompareTo(a.Key));
        }
    }
}
