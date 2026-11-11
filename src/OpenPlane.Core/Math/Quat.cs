using System;
using System.Globalization;

namespace OpenPlane.Core.Math
{
    /// <summary>A rotation quaternion stored as Unity does: x, y, z, w.</summary>
    public readonly struct Quat
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;
        public readonly double W;

        public Quat(double x, double y, double z, double w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        /// <summary>Hamilton product, same convention as Unity's <c>a * b</c> (apply b, then a).</summary>
        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>Rotate a vector, same as Unity's <c>q * v</c>.</summary>
        public static Vec3 operator *(Quat q, Vec3 v)
        {
            double tx = 2 * (q.Y * v.Z - q.Z * v.Y);
            double ty = 2 * (q.Z * v.X - q.X * v.Z);
            double tz = 2 * (q.X * v.Y - q.Y * v.X);
            return new Vec3(
                v.X + q.W * tx + (q.Y * tz - q.Z * ty),
                v.Y + q.W * ty + (q.Z * tx - q.X * tz),
                v.Z + q.W * tz + (q.X * ty - q.Y * tx));
        }

        public Quat Normalized
        {
            get
            {
                double l = System.Math.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-12 ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        /// <summary>The rotation seen in a mirror across x = 0.</summary>
        public Quat MirrorX => new Quat(X, -Y, -Z, W);

        public static Quat AngleAxis(double degrees, Vec3 axis)
        {
            var n = axis.Normalized;
            double h = degrees * System.Math.PI / 360.0;
            double s = System.Math.Sin(h);
            return new Quat(n.X * s, n.Y * s, n.Z * s, System.Math.Cos(h));
        }

        public double[] ToArray() => new[] { X, Y, Z, W };
        public static Quat FromArray(double[] a) => new Quat(a[0], a[1], a[2], a[3]);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.####}, {1:0.####}, {2:0.####}, {3:0.####})", X, Y, Z, W);
    }

    /// <summary>A position and rotation; composes like Unity's Transform hierarchy (no scale).</summary>
    public readonly struct Pose
    {
        public readonly Vec3 Position;
        public readonly Quat Rotation;

        public Pose(Vec3 position, Quat rotation) { Position = position; Rotation = rotation; }

        public static readonly Pose Identity = new Pose(Vec3.Zero, Quat.Identity);

        /// <summary>World pose of a child whose local pose is <paramref name="local"/>.</summary>
        public Pose Compose(Pose local) => new Pose(Position + Rotation * local.Position, Rotation * local.Rotation);

        /// <summary>A point given in this pose's local space, in the parent (world) space.</summary>
        public Vec3 TransformPoint(Vec3 local) => Position + Rotation * local;

        public Vec3 TransformDirection(Vec3 local) => Rotation * local;
    }
}
