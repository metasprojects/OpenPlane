using System;
using System.Collections.Generic;
using System.Linq;
using OpenPlane.Core.Design;
using OpenPlane.Core.Math;

namespace OpenPlane.Core.Physics
{
    /// <summary>Whether the pilot can see out from the cockpit eye, where the game's cockpit camera sits.</summary>
    public static class CockpitView
    {
        /// <summary>
        /// Looks from the eye in nine directions ahead and above (left, centre and right; level, raised and steep). If the
        /// body's skin is within reach in every one of them, the eye is buried in the body and the cockpit view would be the
        /// inside of the hull. Glass, cut-outs and interior pieces don't block the view.
        /// </summary>
        public static bool Blocked(AircraftDesign d, IList<MeshData> meshes, double reach = 60.0)
        {
            if (d.Cockpit == null || d.Cockpit.OwnView == false || meshes == null || meshes.Count == 0) return false;
            var eye = Vec3.FromArray(d.Cockpit.EyeOrDefault());
            int blocked = 0, total = 0;
            foreach (double yaw in new[] { -40.0, 0, 40 })
                foreach (double pitch in new[] { 0.0, 25, 55 })
                {
                    double y = yaw * System.Math.PI / 180, p = pitch * System.Math.PI / 180;
                    var dir = new Vec3(System.Math.Sin(y) * System.Math.Cos(p), System.Math.Sin(p), System.Math.Cos(y) * System.Math.Cos(p));
                    total++;
                    if (HitsSkin(d, meshes, eye, dir, reach)) blocked++;
                    if (total > blocked) return false;   // any clear line of sight: the eye isn't buried in the body
                }
            return true;
        }

        private static bool HitsSkin(AircraftDesign d, IList<MeshData> meshes, Vec3 o, Vec3 dir, double reach)
        {
            foreach (var m in meshes)
            {
                if (m.Role == MeshData.Interior || m.Vertices == null || m.Triangles == null) continue;
                var v = m.Vertices;
                var see = new Dictionary<int, bool>();   // material slot -> see-through
                for (int i = 0; i + 2 < m.Triangles.Length; i += 3)
                {
                    int slot = m.TriangleMaterials != null && i / 3 < m.TriangleMaterials.Length ? m.TriangleMaterials[i / 3] : 0;
                    if (!see.TryGetValue(slot, out bool glass)) see[slot] = glass = DesignMaterials.ForSlot(d, m, slot).Kind == MaterialDef.Glass;
                    if (glass) continue;
                    Vec3 P(int k) => new Vec3(v[k * 3], v[k * 3 + 1], v[k * 3 + 2]);
                    double t = Hit(o, dir, P(m.Triangles[i]), P(m.Triangles[i + 1]), P(m.Triangles[i + 2]));
                    if (t > 1e-4 && t < reach) return true;
                }
            }
            return false;
        }

        /// <summary>Distance along the ray to the triangle (either side), or -1 (Moller-Trumbore).</summary>
        private static double Hit(Vec3 o, Vec3 dir, Vec3 a, Vec3 b, Vec3 c)
        {
            var e1 = b - a; var e2 = c - a;
            var p = Vec3.Cross(dir, e2);
            double det = Vec3.Dot(e1, p);
            if (System.Math.Abs(det) < 1e-12) return -1;
            double inv = 1 / det;
            var s = o - a;
            double u = Vec3.Dot(s, p) * inv;
            if (u < 0 || u > 1) return -1;
            var q = Vec3.Cross(s, e1);
            double w = Vec3.Dot(dir, q) * inv;
            if (w < 0 || u + w > 1) return -1;
            return Vec3.Dot(e2, q) * inv;
        }
    }
}
