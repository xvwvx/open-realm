using System;

namespace MdxPreview.Core.Math3D
{
    /// <summary>
    /// Minimal dependency-free vector/quaternion/matrix math for the core layer.
    /// The core layer never references Godot types so it can be unit tested from a
    /// plain console project (see tests/CoreTests).
    /// Conventions follow the engine reference (open-realm): right handed, column
    /// vectors, row-major 4x4 storage with translation in row 3 (column-vector
    /// multiplication m * v).
    /// </summary>
    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 One = new Vec3(1, 1, 1);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public float this[int i]
        {
            get => i == 0 ? X : i == 1 ? Y : Z;
            set { if (i == 0) X = value; else if (i == 1) Y = value; else Z = value; }
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => a * s;
        public static Vec3 operator *(Vec3 a, Vec3 b) => new Vec3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        public static Vec3 operator /(Vec3 a, float s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public float LengthSquared => X * X + Y * Y + Z * Z;
        public float Length => MathF.Sqrt(LengthSquared);

        public Vec3 Normalized()
        {
            float len = Length;
            return len > 1e-20f ? this / len : Zero;
        }

        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;

        public override string ToString() => $"({X:0.#####}, {Y:0.#####}, {Z:0.#####})";
    }

    public struct Quat
    {
        public float X, Y, Z, W;

        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public float LengthSquared => X * X + Y * Y + Z * Z + W * W;

        public Quat Normalized()
        {
            float len = MathF.Sqrt(LengthSquared);
            if (len <= 1e-20f) return Identity;
            float inv = 1f / len;
            return new Quat(X * inv, Y * inv, Z * inv, W * inv);
        }

        public static float Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        public static Quat Slerp(Quat a, Quat b, float t)
        {
            float dot = Dot(a, b);
            if (dot < 0f)
            {
                b = new Quat(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }
            if (dot > 0.9995f)
            {
                // Near-parallel: linear interpolation plus renormalisation.
                return new Quat(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t,
                    a.W + (b.W - a.W) * t).Normalized();
            }
            float theta0 = MathF.Acos(Math.Clamp(dot, -1f, 1f));
            float theta = theta0 * t;
            float sinTheta = MathF.Sin(theta);
            float sinTheta0 = MathF.Sin(theta0);
            float s0 = MathF.Cos(theta) - dot * sinTheta / sinTheta0;
            float s1 = sinTheta / sinTheta0;
            return new Quat(
                a.X * s0 + b.X * s1,
                a.Y * s0 + b.Y * s1,
                a.Z * s0 + b.Z * s1,
                a.W * s0 + b.W * s1).Normalized();
        }

        /// <summary>Rotation about an arbitrary (assumed normalised) axis, radians.</summary>
        public static Quat FromAxisAngle(Vec3 axis, float radians)
        {
            float half = radians * 0.5f;
            float s = MathF.Sin(half);
            return new Quat(axis.X * s, axis.Y * s, axis.Z * s, MathF.Cos(half));
        }

        /// <summary>
        /// Euler angles in degrees composed as Rz * Ry * Rx, matching the engine's
        /// Matrix4_rotate(..., ROTATE_XYZ) order.
        /// </summary>
        public static Quat FromEulerXYZ(float xDegrees, float yDegrees, float zDegrees)
        {
            const float deg2rad = MathF.PI / 180f;
            Quat qx = FromAxisAngle(Vec3.UnitX, xDegrees * deg2rad);
            Quat qy = FromAxisAngle(Vec3.UnitY, yDegrees * deg2rad);
            Quat qz = FromAxisAngle(Vec3.UnitZ, zDegrees * deg2rad);
            return qz * qy * qx;
        }

        public Mat4 ToMatrix() => Mat4.FromQuat(this);

        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})";
    }

    /// <summary>
    /// Row-major 4x4 matrix with the translation in the fourth <b>column</b>
    /// (M14/M24/M34) and rows acting as basis vectors, so <c>M * v</c> and
    /// <c>M1 * M2</c> match the engine reference (shared/source/matrix4.c) and
    /// map 1:1 onto Godot's <c>Transform3D</c> (Basis rows + origin).
    /// </summary>
    public struct Mat4
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static readonly Mat4 Identity = new Mat4
        {
            M11 = 1, M22 = 1, M33 = 1, M44 = 1
        };

        public static Mat4 FromTranslation(Vec3 t) => new Mat4
        {
            M11 = 1, M22 = 1, M33 = 1, M44 = 1,
            M14 = t.X, M24 = t.Y, M34 = t.Z
        };

        public static Mat4 FromScale(Vec3 s) => new Mat4
        {
            M11 = s.X, M22 = s.Y, M33 = s.Z, M44 = 1
        };

        public static Mat4 FromQuat(Quat q)
        {
            q = q.Normalized();
            float x = q.X, y = q.Y, z = q.Z, w = q.W;
            float xx = x * x, yy = y * y, zz = z * z;
            float xy = x * y, xz = x * z, yz = y * z;
            float wx = w * x, wy = w * y, wz = w * z;

            return new Mat4
            {
                M11 = 1 - 2 * (yy + zz),
                M12 = 2 * (xy - wz),
                M13 = 2 * (xz + wy),
                M21 = 2 * (xy + wz),
                M22 = 1 - 2 * (xx + zz),
                M23 = 2 * (yz - wx),
                M31 = 2 * (xz - wy),
                M32 = 2 * (yz + wx),
                M33 = 1 - 2 * (xx + yy),
                M44 = 1
            };
        }

        /// <summary>
        /// MDX node composition: <c>T(translation + pivot) * R * S * T(-pivot)</c>,
        /// matching Matrix4_from_rotation_translation_scale_origin plus
        /// Matrix4_from_rotation_origin in the engine reference.
        /// </summary>
        public static Mat4 ComposeOrigin(Vec3 translation, Quat rotation, Vec3 scale, Vec3 pivot)
        {
            Mat4 r = FromQuat(rotation);
            r.M11 *= scale.X; r.M12 *= scale.X; r.M13 *= scale.X;
            r.M21 *= scale.Y; r.M22 *= scale.Y; r.M23 *= scale.Y;
            r.M31 *= scale.Z; r.M32 *= scale.Z; r.M33 *= scale.Z;

            // Column 4 = (translation + pivot) - (R*S * pivot)
            r.M14 = translation.X + pivot.X - (r.M11 * pivot.X + r.M12 * pivot.Y + r.M13 * pivot.Z);
            r.M24 = translation.Y + pivot.Y - (r.M21 * pivot.X + r.M22 * pivot.Y + r.M23 * pivot.Z);
            r.M34 = translation.Z + pivot.Z - (r.M31 * pivot.X + r.M32 * pivot.Y + r.M33 * pivot.Z);
            r.M44 = 1;
            return r;
        }

        /// <summary>Translation * Rotation * Scale composition used by MDX nodes.</summary>
        public static Mat4 Compose(Vec3 translation, Quat rotation, Vec3 scale)
        {
            Mat4 r = FromQuat(rotation);
            r.M11 *= scale.X; r.M12 *= scale.X; r.M13 *= scale.X;
            r.M21 *= scale.Y; r.M22 *= scale.Y; r.M23 *= scale.Y;
            r.M31 *= scale.Z; r.M32 *= scale.Z; r.M33 *= scale.Z;
            r.M14 = translation.X; r.M24 = translation.Y; r.M34 = translation.Z; r.M44 = 1;
            return r;
        }

        public static Mat4 operator *(Mat4 a, Mat4 b)
        {
            Mat4 o;
            o.M11 = a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31 + a.M14 * b.M41;
            o.M12 = a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32 + a.M14 * b.M42;
            o.M13 = a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33 + a.M14 * b.M43;
            o.M14 = a.M11 * b.M14 + a.M12 * b.M24 + a.M13 * b.M34 + a.M14 * b.M44;

            o.M21 = a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31 + a.M24 * b.M41;
            o.M22 = a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32 + a.M24 * b.M42;
            o.M23 = a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33 + a.M24 * b.M43;
            o.M24 = a.M21 * b.M14 + a.M22 * b.M24 + a.M23 * b.M34 + a.M24 * b.M44;

            o.M31 = a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31 + a.M34 * b.M41;
            o.M32 = a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32 + a.M34 * b.M42;
            o.M33 = a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33 + a.M34 * b.M43;
            o.M34 = a.M31 * b.M14 + a.M32 * b.M24 + a.M33 * b.M34 + a.M34 * b.M44;

            o.M41 = a.M41 * b.M11 + a.M42 * b.M21 + a.M43 * b.M31 + a.M44 * b.M41;
            o.M42 = a.M41 * b.M12 + a.M42 * b.M22 + a.M43 * b.M32 + a.M44 * b.M42;
            o.M43 = a.M41 * b.M13 + a.M42 * b.M23 + a.M43 * b.M33 + a.M44 * b.M43;
            o.M44 = a.M41 * b.M14 + a.M42 * b.M24 + a.M43 * b.M34 + a.M44 * b.M44;
            return o;
        }

        public Vec3 TransformPoint(Vec3 v) => new Vec3(
            M11 * v.X + M12 * v.Y + M13 * v.Z + M14,
            M21 * v.X + M22 * v.Y + M23 * v.Z + M24,
            M31 * v.X + M32 * v.Y + M33 * v.Z + M34);

        public Vec3 TransformDirection(Vec3 v) => new Vec3(
            M11 * v.X + M12 * v.Y + M13 * v.Z,
            M21 * v.X + M22 * v.Y + M23 * v.Z,
            M31 * v.X + M32 * v.Y + M33 * v.Z);

        public Vec3 Translation => new Vec3(M14, M24, M34);

        /// <summary>
        /// Inverse of an affine transform (rotation/scale in the 3x3 block plus
        /// translation). Every matrix the plugin builds is affine; a singular
        /// 3x3 block falls back to the identity.
        /// </summary>
        public Mat4 Inverse()
        {
            float a = M11, b = M12, c = M13;
            float d = M21, e = M22, f = M23;
            float g = M31, h = M32, i = M33;

            float det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (MathF.Abs(det) < 1e-20f) return Identity;
            float inv = 1f / det;

            Mat4 o = default;
            o.M11 = (e * i - f * h) * inv;
            o.M12 = (c * h - b * i) * inv;
            o.M13 = (b * f - c * e) * inv;
            o.M21 = (f * g - d * i) * inv;
            o.M22 = (a * i - c * g) * inv;
            o.M23 = (c * d - a * f) * inv;
            o.M31 = (d * h - e * g) * inv;
            o.M32 = (b * g - a * h) * inv;
            o.M33 = (a * e - b * d) * inv;

            Vec3 t = Translation;
            o.M14 = -(o.M11 * t.X + o.M12 * t.Y + o.M13 * t.Z);
            o.M24 = -(o.M21 * t.X + o.M22 * t.Y + o.M23 * t.Z);
            o.M34 = -(o.M31 * t.X + o.M32 * t.Y + o.M33 * t.Z);
            o.M44 = 1;
            return o;
        }
        public override string ToString() =>
            $"[{M11:0.##} {M12:0.##} {M13:0.##} {M14:0.##}; {M21:0.##} {M22:0.##} {M23:0.##} {M24:0.##}; " +
            $"{M31:0.##} {M32:0.##} {M33:0.##} {M34:0.##}; {M41:0.##} {M42:0.##} {M43:0.##} {M44:0.##}]";
    }
}
