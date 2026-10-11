using System;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Runtime
{
    /// <summary>One trail edge: the two authored half-height endpoints plus its tint.</summary>
    public struct MdxRibbonEdge
    {
        public Vec3 Above;
        public Vec3 Below;
        public Vec3 Color;
        public float Alpha;
        public float Age;
    }

    /// <summary>Strip vertex produced by <see cref="MdxRibbonMath.BuildStrip"/>.</summary>
    public struct MdxRibbonVertex
    {
        public Vec3 Position;
        public float U;
        public float V;
        public Vec3 Color;
        public float Alpha;
    }

    /// <summary>
    /// Per-emitter edge history — a port of the engine trail store
    /// (renderer/r_trail.h + r_trail.c). A ring buffer of edges, aged by dt,
    /// pulled down by gravity, expired at the lifespan and fed by a rate*dt
    /// accumulator that clamps at 2 so a hitch cannot stack coincident edges.
    /// The same rules are reachable without Godot so the self-test can drive a
    /// trail directly.
    /// </summary>
    public sealed class MdxRibbonTrail
    {
        /// <summary>TRAIL_MAX_EDGES.</summary>
        public const int MaxEdges = 48;

        /// <summary>TRAIL_STALE_MS: a longer gap means the owner was reused.</summary>
        public const double StaleMs = 250.0;

        /// <summary>R_TrailAdvance clamps the accumulator before emitting.</summary>
        public const float MaxAccumulator = 2f;

        readonly MdxRibbonEdge[] _edges = new MdxRibbonEdge[MaxEdges];

        /// <summary>Ring write cursor.</summary>
        public int Head { get; private set; }

        /// <summary>Number of live edges.</summary>
        public int Count { get; private set; }

        public float Accumulator { get; private set; }

        public double StampMs { get; private set; }

        public bool HasStamp { get; private set; }

        public void Clear()
        {
            Array.Clear(_edges, 0, _edges.Length);
            Head = 0;
            Count = 0;
            Accumulator = 0f;
            StampMs = 0.0;
            HasStamp = false;
        }

        int IndexOf(int order)
        {
            int index = (Head - Count + order) % MaxEdges;
            return index < 0 ? index + MaxEdges : index;
        }

        /// <summary>Edge by age order: 0 is the oldest live edge.</summary>
        public bool TryGetEdge(int order, out MdxRibbonEdge edge)
        {
            edge = default;
            if (order < 0 || order >= Count) return false;
            edge = _edges[IndexOf(order)];
            return true;
        }

        /// <summary>
        /// R_TrailAdvance. <paramref name="nowMs"/> is the monotonic preview clock and
        /// <paramref name="deltaSeconds"/> the frame step: a repeat call inside the same
        /// millisecond gets dt = 0 and only re-emits nothing, while a gap past
        /// <see cref="StaleMs"/> clears the trail (owner/sequence reuse).
        /// Returns the live edge count.
        /// </summary>
        public int Advance(
            Vec3 above,
            Vec3 below,
            Vec3 color,
            float alpha,
            float lifespan,
            float rate,
            float gravity,
            double nowMs,
            float deltaSeconds)
        {
            if (lifespan <= 0f)
            {
                Count = 0;
                Accumulator = 0f;
                return 0;
            }

            double gap = nowMs - StampMs;
            if (HasStamp && gap > StaleMs) Clear();
            float dt = gap > 0.0 ? MathF.Max(deltaSeconds, 0f) : 0f;
            StampMs = nowMs;
            HasStamp = true;

            for (int i = 0; i < Count; i++)
            {
                int index = IndexOf(i);
                _edges[index].Age += dt;
                // Gravity pulls live edges along -Z (MDX space is Z-up).
                _edges[index].Above.Z -= gravity * dt;
                _edges[index].Below.Z -= gravity * dt;
            }

            while (Count > 0)
            {
                int oldest = IndexOf(0);
                if (_edges[oldest].Age < lifespan) break;
                Count--;
            }

            if (rate > 0f && dt > 0f)
            {
                Accumulator = MathF.Min(Accumulator + rate * dt, MaxAccumulator);
                while (Accumulator >= 1f)
                {
                    Accumulator -= 1f;
                    if (Count >= MaxEdges) Count--; // evict the oldest before overwriting it
                    _edges[Head] = new MdxRibbonEdge
                    {
                        Above = above,
                        Below = below,
                        Color = color,
                        Alpha = alpha,
                        Age = 0f,
                    };
                    Head = (Head + 1) % MaxEdges;
                    Count++;
                }
            }

            return Count;
        }
    }

    /// <summary>
    /// RIBB strip synthesis (renderer/r_trail.c R_TrailStripVerts). U is age based
    /// inside the atlas column so adding or expiring an edge never rescales the rest
    /// of the unwrap, and the vertical range is the authored textureSlot's row.
    /// </summary>
    public static class MdxRibbonMath
    {
        public const float Epsilon = 0.0001f;

        /// <summary>Atlas cell origin for a texture slot (column-major like the engine).</summary>
        public static void CellOrigin(int slot, int columns, int rows, out float cellU, out float cellV)
        {
            if (columns < 1) columns = 1;
            if (rows < 1) rows = 1;
            int value = slot < 0 ? 0 : slot;
            cellU = (float)(value % columns) / columns;
            cellV = (float)(value / columns) / rows;
        }

        /// <summary>U = cellU + min(1, age/lifespan) / columns.</summary>
        public static float EdgeU(float age, float lifespan, int columns, int slot)
        {
            if (columns < 1) columns = 1;
            CellOrigin(slot, columns, 1, out float cellU, out _);
            if (lifespan <= Epsilon) return cellU;
            float t = age / lifespan;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return cellU + t / columns;
        }

        /// <summary>Vertical cell range; <paramref name="top"/> is the smaller V.</summary>
        public static void CellRange(int slot, int columns, int rows, out float top, out float bottom)
        {
            if (rows < 1) rows = 1;
            CellOrigin(slot, columns, rows, out _, out float cellV);
            top = cellV;
            bottom = cellV + 1f / rows;
        }

        /// <summary>
        /// One quad (6 vertices) per consecutive edge pair, oldest edge first. Vertex
        /// order, winding and UVs are copied from R_TrailQuad so the strip unwraps
        /// exactly like the engine: the older edge takes the larger U, the "above"
        /// endpoints take the cell's upper V.
        /// </summary>
        public static int BuildStrip(
            MdxRibbonTrail trail,
            float lifespan,
            int columns,
            int rows,
            int slot,
            MdxRibbonVertex[] output)
        {
            if (trail == null || output == null || trail.Count < 2 || lifespan <= 0f) return 0;

            CellRange(slot, columns, rows, out float top, out float bottom);
            int used = 0;
            for (int i = 0; i + 1 < trail.Count && used + 6 <= output.Length; i++)
            {
                if (!trail.TryGetEdge(i, out MdxRibbonEdge older)) break;
                if (!trail.TryGetEdge(i + 1, out MdxRibbonEdge newer)) break;

                float uOlder = EdgeU(older.Age, lifespan, columns, slot);
                float uNewer = EdgeU(newer.Age, lifespan, columns, slot);

                output[used + 0] = new MdxRibbonVertex { Position = older.Above, U = uOlder, V = top, Color = older.Color, Alpha = older.Alpha };
                output[used + 1] = new MdxRibbonVertex { Position = older.Below, U = uOlder, V = bottom, Color = older.Color, Alpha = older.Alpha };
                output[used + 2] = new MdxRibbonVertex { Position = newer.Below, U = uNewer, V = bottom, Color = newer.Color, Alpha = newer.Alpha };
                output[used + 3] = new MdxRibbonVertex { Position = older.Above, U = uOlder, V = top, Color = older.Color, Alpha = older.Alpha };
                output[used + 4] = new MdxRibbonVertex { Position = newer.Below, U = uNewer, V = bottom, Color = newer.Color, Alpha = newer.Alpha };
                output[used + 5] = new MdxRibbonVertex { Position = newer.Above, U = uNewer, V = top, Color = newer.Color, Alpha = newer.Alpha };
                used += 6;
            }
            return used;
        }
    }
}
