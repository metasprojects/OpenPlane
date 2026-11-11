using System;
using System.Globalization;

namespace OpenPlane.Core.Math
{
    /// <summary>A 3D vector in Unity axes (x right, y up, z forward), metres.</summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 Forward = new Vec3(0, 0, 1);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);
        public static readonly Vec3 Right = new Vec3(1, 0, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public double Length => System.Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized { get { var l = Length; return l > 1e-12 ? this / l : Zero; } }

        /// <summary>Reflection across the aircraft's centre plane (x = 0).</summary>
        public Vec3 MirrorX => new Vec3(-X, Y, Z);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
        public static Vec3 Min(Vec3 a, Vec3 b) => new Vec3(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));
        public static Vec3 Max(Vec3 a, Vec3 b) => new Vec3(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));

        public double[] ToArray() => new[] { X, Y, Z };
        public static Vec3 FromArray(double[] a) => new Vec3(a[0], a[1], a[2]);

        public bool Equals(Vec3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => (X, Y, Z).GetHashCode();
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }
}
