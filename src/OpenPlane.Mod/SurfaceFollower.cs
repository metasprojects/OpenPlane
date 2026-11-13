using UnityEngine;

namespace OpenPlane.Mod
{
    internal static partial class DesignVisuals
    {
        /// <summary>A copy of a mesh with every point moved by <paramref name="by"/> (to put its pivot on a hinge).</summary>
        internal static Mesh Shifted(Mesh source, Vector3 by)
        {
            var m = Object.Instantiate(source);
            m.name = source.name;
            var v = m.vertices;
            for (int i = 0; i < v.Length; i++) v[i] += by;
            m.vertices = v;
            m.RecalculateBounds();
            return m;
        }
    }

    /// <summary>
    /// Turns a design's control surface about its hinge as the stock control surface it rides on turns: the stock surface's
    /// deflection about its own hinge (its visible mesh against its resting rotation) is copied onto the design's hinge.
    /// <see cref="Sign"/> is +1 when the two hinges point the same way.
    /// </summary>
    internal sealed class SurfaceFollower : MonoBehaviour
    {
        public Transform? Stock;
        public Quaternion StockRest = Quaternion.identity;
        public Vector3 Axis = Vector3.right;
        public float Sign = 1f;
        private Quaternion _rest;

        private void Start() { _rest = transform.localRotation; }

        private void LateUpdate()
        {
            if (Stock == null) return;
            var q = Quaternion.Inverse(StockRest) * Stock.localRotation;
            q.ToAngleAxis(out float angle, out Vector3 axis);
            if (float.IsNaN(angle) || float.IsInfinity(axis.x)) return;
            if (angle > 180f) angle -= 360f;
            if (Vector3.Dot(axis, Vector3.right) < 0) angle = -angle;
            transform.localRotation = Quaternion.AngleAxis(Sign * angle, Axis) * _rest;
        }
    }
}
