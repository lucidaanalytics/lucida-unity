using System.Collections.Generic;

namespace Lucida
{
    /// <summary>
    /// Engine-free half of the 3D scene wireframe: turns the projected corners of an object's bounds into a screen
    /// rectangle. The Unity adapter projects the 8 corners with <c>Camera.WorldToScreenPoint</c> and calls this.
    /// </summary>
    public static class SceneBox
    {
        /// <summary>
        /// <paramref name="corners"/> are {x, y, z}: screen pixels with a bottom-left origin and z the distance in
        /// front of the camera. Returns {left, top, width, height, nearest z} with a top-left origin, clipped to the
        /// screen, or null when the box is behind the camera or off screen.
        /// shortcut: corners behind the camera are dropped, so a box crossing the near plane is sized by its front
        /// corners only; clip against the frustum if that ever matters.
        /// </summary>
        public static float[] Rect(IList<float[]> corners, int screenW, int screenH)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, near = float.MaxValue;
            var any = false;
            foreach (var c in corners)
            {
                if (c[2] <= 0) continue;
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
