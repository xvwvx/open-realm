using System;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Runtime
{
    /// <summary>
    /// Deterministic 32-bit xorshift generator. The engine uses
    /// <c>rand() / (float)RAND_MAX</c>; the preview replaces it with a seeded
    /// stream so runs (and screenshot tests) are reproducible while keeping the
    /// same [0,1) contract — every draw site below mirrors the exact order and
    /// count of <c>rand()</c> calls in the C reference.
    /// </summary>
    public struct MdxParticleRng
    {
        uint _state;

        public MdxParticleRng(uint seed)
        {
            _state = seed == 0u ? 0x9E3779B9u : seed;
        }

        public uint State => _state;

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        /// <summary>Uniform sample in [0,1) with 24 bits of mantissa.</summary>
        public float NextUnit() => (NextUInt() >> 8) * (1f / 16777216f);
    }

    /// <summary>One authored PRE2 UV animation triple (Start / End / Repeat).</summary>
    public struct MdxParticleUvRange
    {
        public uint Start;
        public uint End;
        public uint Repeat;

        public MdxParticleUvRange(uint start, uint end, uint repeat)
        {
            Start = start;
            End = end;
            Repeat = repeat;
        }

        /// <summary>An "empty" triple (all zero) is what the format writes when unused.</summary>
        public bool IsUsed => End > Start && Repeat > 0u;
    }

    /// <summary>
    /// One atlas cell of a Rows x Columns sprite sheet. <see cref="Top"/> is always
    /// smaller than <see cref="Bottom"/>: the engine's V grows toward the bottom of
    /// the authored image, so a quad's upper vertices take <see cref="Top"/> and its
    /// lower vertices take <see cref="Bottom"/> (see FX_GetFrame + R_AddParticle in
    /// renderer/r_particles.c).
    /// </summary>
    public readonly struct MdxAtlasCell
    {
        public readonly int Frame;
        public readonly float Left;
        public readonly float Top;
        public readonly float Right;
        public readonly float Bottom;

        public MdxAtlasCell(int frame, float left, float top, float right, float bottom)
        {
            Frame = frame;
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    /// <summary>
    /// Emitter properties resolved for the current frame (static value or sampled
    /// track). Mirrors <c>mdx_pctx_t</c> in r_mdx_particles.c plus the PREM-only
    /// <see cref="Longitude"/>.
    /// </summary>
    public struct MdxParticleParams
    {
        public bool Visible;
        public float Speed;
        public float Variation;
        public float Latitude;
        public float Longitude;
        public float Gravity;
        public float EmissionRate;
        public float Length;
        public float Width;
    }

    /// <summary>
    /// Per-particle appearance snapshot taken at spawn time. The C reference copies
    /// this block into every <c>cparticle_t</c> (colours, alpha, size curve, atlas
    /// size and the four UV animations), so a particle keeps the appearance the
    /// emitter had on the frame it was born. The preview shares one immutable
    /// snapshot per emitter per spawned frame instead of duplicating it per
    /// particle.
    /// </summary>
    public sealed class MdxParticleAppearance
    {
        public Vec3 Color0 = Vec3.One;
        public Vec3 Color1 = Vec3.One;
        public Vec3 Color2 = Vec3.One;
        public float Alpha0 = 1f;
        public float Alpha1 = 1f;
        public float Alpha2 = 1f;
        public float MidTime;
        public float Size0 = 1f;
        public float Size1 = 1f;
        public float Size2 = 1f;
        public int Rows = 1;
        public int Columns = 1;
        public MdxParticleUvRange LifeSpanUv;
        public MdxParticleUvRange DecayUv;
        public MdxParticleUvRange TailUv;
        public MdxParticleUvRange TailDecayUv;

        public void SetColors(int index, Vec3 color)
        {
            if (index == 0) Color0 = color; else if (index == 1) Color1 = color; else Color2 = color;
        }

        public void SetAlphas(int index, float alpha)
        {
            if (index == 0) Alpha0 = alpha; else if (index == 1) Alpha1 = alpha; else Alpha2 = alpha;
        }

        public void SetSizes(int index, float size)
        {
            if (index == 0) Size0 = size; else if (index == 1) Size1 = size; else Size2 = size;
        }
    }

    /// <summary>
    /// Godot-free particle mathematics shared by <see cref="MdxParticleSystem"/> and
    /// the ParticleProbe self-test. Every routine is a direct port of the reference
    /// (renderer/r_emit.h, renderer/r_particles.c, games/warcraft-3/renderer/mdx/
    /// r_mdx_particles.c) so the behaviour can be verified without Godot.
    /// </summary>
    public static class MdxParticleMath
    {
        public const float Epsilon = 0.0001f;
        public const float DegToRad = MathF.PI / 180f;

        /// <summary>R_EMIT_PARTICLE_BUDGET: particles one emitter may spawn per call.</summary>
        public const int EmissionBudgetPerFrame = 256;

        /// <summary>R_EmitParticles clamps catch-up time so a hitch cannot dump a huge burst.</summary>
        public const float MaxCatchUpSeconds = 0.1f;

        public static float Clamp01(float value) => value < 0f ? 0f : (value > 1f ? 1f : value);

        public static byte ToByte(float value)
        {
            int scaled = (int)MathF.Round(value * 255f);
            return (byte)(scaled < 0 ? 0 : (scaled > 255 ? 255 : scaled));
        }

        /// <summary>k = time / lifespan, clamped (FX_BlendColor uses this ratio).</summary>
        public static float NormalizedLife(float time, float lifespan)
            => lifespan > Epsilon ? Clamp01(time / lifespan) : 0f;

        // ---- emission ------------------------------------------------------------------

        /// <summary>
        /// FX_GenerateRandomDirection(latitude): the direction is uniform in the polar
        /// angle phi in [0, latitude] (radians) and uniform in azimuth, so the cone
        /// half-angle is exactly "latitude" and latitude 0 emits straight along +Z.
        /// </summary>
        public static Vec3 RandomConeDirection(ref MdxParticleRng rng, float latitudeRadians)
        {
            float theta = rng.NextUnit() * 2f * MathF.PI;
            float phi = rng.NextUnit() * MathF.Max(latitudeRadians, 0f);
            return new Vec3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Sin(phi) * MathF.Sin(theta),
                MathF.Cos(phi));
        }

        /// <summary>
        /// PREM variant: a non-zero longitude narrower than a half turn restricts the
        /// azimuth to +/- longitude instead of the full circle. PRE2 always passes 0
        /// and therefore matches <see cref="RandomConeDirection(ref MdxParticleRng, float)"/>
        /// exactly.
        /// </summary>
        public static Vec3 RandomConeDirection(ref MdxParticleRng rng, float latitudeRadians, float longitudeRadians)
        {
            float theta = longitudeRadians > 0f && longitudeRadians < MathF.PI
                ? (rng.NextUnit() * 2f - 1f) * longitudeRadians
                : rng.NextUnit() * 2f * MathF.PI;
            float phi = rng.NextUnit() * MathF.Max(latitudeRadians, 0f);
            return new Vec3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Sin(phi) * MathF.Sin(theta),
                MathF.Cos(phi));
        }

        /// <summary>
        /// mdx_spawn_particle draws one number that drives both the spawn X offset and
        /// the speed variation, then one more for the Y offset. Splitting the draws
        /// into these helpers keeps that sharing explicit and testable.
        /// </summary>
        public static float SpawnRandom(ref MdxParticleRng rng) => rng.NextUnit();

        /// <summary>((r-0.5)*length, (rand-0.5)*width, 0) relative to the node pivot.</summary>
        public static Vec3 SpawnOffset(float r, ref MdxParticleRng rng, float length, float width)
            => new Vec3((r - 0.5f) * length, (rng.NextUnit() - 0.5f) * width, 0f);

        /// <summary>speed + (r-0.5)*variation, sharing the spawn draw above.</summary>
        public static float SpawnSpeed(float r, float speed, float variation)
            => speed + (r - 0.5f) * variation;

        /// <summary>
        /// Frame-relative accumulator emission (renderer/r_emit.h R_EmitParticles).
        /// Returns the number of particles to spawn this call and stores the carried
        /// fraction back into <paramref name="accumulator"/>.
        /// </summary>
        public static int AdvanceAccumulator(ref float accumulator, float rate, float deltaSeconds)
        {
            if (!(rate > 0f) || !(deltaSeconds > 0f)) return 0;
            if (!float.IsFinite(rate) || !float.IsFinite(accumulator) || accumulator < 0f)
            {
                accumulator = 0f;
                return 0;
            }

            double pending = accumulator + (double)rate * Math.Min(deltaSeconds, MaxCatchUpSeconds);
            int emitted = 0;
            while (pending >= 1.0 && emitted < EmissionBudgetPerFrame)
            {
                pending -= 1.0;
                emitted++;
            }
            if (pending >= 1.0) pending %= 1.0; // budget exceeded: drop whole emissions only
            accumulator = (float)pending;
            return emitted;
        }

        /// <summary>
        /// Squirt burst emission. The C reference has no squirt path at all (it always
        /// uses the fractional accumulator), so this follows the authored intent of the
        /// flag: the whole quota of <paramref name="rate"/> particles is released at
        /// once, and the emitter then stays silent for one particle lifespan — the
        /// retail use is a one-shot death/explosion burst (e.g. BlizParticle02).
        /// </summary>
        public static int AdvanceSquirt(ref float accumulator, float rate, float lifespan, float deltaSeconds, int cap)
        {
            if (!(rate > 0f) || !(deltaSeconds > 0f)) return 0;
            if (!float.IsFinite(accumulator) || accumulator < 0f) accumulator = 0f;

            float interval = MathF.Max(lifespan, 0.05f);
            accumulator += deltaSeconds;
            if (accumulator < interval) return 0;
            accumulator %= interval;

            int count = (int)MathF.Max(1f, MathF.Round(rate));
            if (cap > 0 && count > cap) count = cap;
            return count;
        }

        // ---- kinematics ----------------------------------------------------------------

        /// <summary>
        /// org(t) = org0 + vel0*t + 1/2*accel*t^2 (R_DrawParticles builds exactly this
        /// closed form; the original engine integrates the same parabola per frame).
        /// </summary>
        public static Vec3 PositionAt(Vec3 origin, Vec3 velocity, Vec3 accel, float time)
            => origin + (velocity + accel * (0.5f * time)) * time;

        // ---- appearance interpolation ---------------------------------------------------

        /// <summary>
        /// FX_BlendFloat / FX_BlendColor: three values split at the middle time. The
        /// engine's version divides by midtime (or 1-midtime) unguarded and produces
        /// NaN for a degenerate middle time; the preview guards the divisors the same
        /// way FX_GetFrame already does.
        /// </summary>
        public static float BlendTriple(float start, float middle, float end, float k, float mid)
        {
            k = Clamp01(k);
            mid = Clamp01(mid);
            if (k > mid)
                return middle + (end - middle) * ((k - mid) / MathF.Max(1f - mid, Epsilon));
            return start + (middle - start) * (k / MathF.Max(mid, Epsilon));
        }

        public static Vec3 BlendTriple(Vec3 start, Vec3 middle, Vec3 end, float k, float mid)
            => new Vec3(
                BlendTriple(start.X, middle.X, end.X, k, mid),
                BlendTriple(start.Y, middle.Y, end.Y, k, mid),
                BlendTriple(start.Z, middle.Z, end.Z, k, mid));

        public static byte BlendTripleByte(byte start, byte middle, byte end, float k, float mid)
            => ToByte(BlendTriple(start / 255f, middle / 255f, end / 255f, k, mid));

        // ---- sprite sheet UVs -----------------------------------------------------------

        /// <summary>Engine cell layout for one frame index (FX_GetFrame's u/v split).</summary>
        public static MdxAtlasCell AtlasCell(int frame, int rows, int columns)
        {
            if (columns < 1) columns = 1;
            if (rows < 1) rows = 1;
            int total = columns * rows;
            if (frame < 0) frame = 0;
            if (frame >= total) frame = total - 1;
            int column = frame % columns;
            int row = frame / columns;
            return new MdxAtlasCell(
                frame,
                (float)column / columns,
                (float)row / rows,
                (float)(column + 1) / columns,
                (float)(row + 1) / rows);
        }

        /// <summary>
        /// FX_GetFrame's default branch: the sheet walks every cell once across the
        /// particle's own lifetime, so independent particles do not strobe in sync.
        /// </summary>
        public static int DefaultAtlasFrame(float k, int rows, int columns)
        {
            if (columns < 1) columns = 1;
            if (rows < 1) rows = 1;
            int total = columns * rows;
            int frame = (int)(Clamp01(k) * total);
            if (frame >= total) frame = total - 1;
            if (frame < 0) frame = 0;
            return frame;
        }

        /// <summary>
        /// One authored Start/End/Repeat animation walk. Start..End is the inclusive
        /// cell range and Repeat is how many times that range is played across the
        /// phase (Repeat 0 means "unused", treated as a single pass when the range is
        /// valid). An empty range returns Start unchanged.
        /// </summary>
        public static int AnimAtlasFrame(uint start, uint end, uint repeat, float t)
        {
            int first = start > 4096u ? 4096 : (int)start;
            int last = end > 4096u ? 4096 : (int)end;
            if (last <= first) return first;
            int span = last - first + 1;
            int cycles = repeat == 0u ? 1 : (repeat > 1024u ? 1024 : (int)repeat);
            int steps = span * cycles;
            int step = (int)(Clamp01(t) * steps);
            if (step >= steps) step = steps - 1;
            if (step < 0) step = 0;
            return first + step % span;
        }

        /// <summary>
        /// Atlas cell for the current particle time. The first animation (PRE2
        /// LifeSpanUVAnim for a head quad, TailUVAnim for a tail quad) covers the life
        /// up to the middle time, the second (DecayUVAnim / TailDecayUVAnim) covers the
        /// decay half; when an animation is empty or only one of the pair is authored
        /// the walk falls back to the engine's whole-lifetime cell sweep so unauthored
        /// emitters keep the reference behaviour exactly.
        /// </summary>
        public static MdxAtlasCell ResolveAtlasCell(
            int rows,
            int columns,
            float k,
            float mid,
            MdxParticleUvRange first,
            MdxParticleUvRange second)
        {
            k = Clamp01(k);
            mid = Clamp01(mid);
            bool firstUsed = first.IsUsed;
            bool secondUsed = second.IsUsed;

            if (firstUsed && k <= mid)
                return AtlasCell(AnimAtlasFrame(first.Start, first.End, first.Repeat, mid > Epsilon ? k / mid : 1f), rows, columns);
            if (secondUsed && k > mid)
                return AtlasCell(AnimAtlasFrame(second.Start, second.End, second.Repeat, (1f - mid) > Epsilon ? (k - mid) / (1f - mid) : 1f), rows, columns);
            if (firstUsed)
                return AtlasCell(AnimAtlasFrame(first.Start, first.End, first.Repeat, k), rows, columns);
            if (secondUsed)
                return AtlasCell(AnimAtlasFrame(second.Start, second.End, second.Repeat, k), rows, columns);
            return AtlasCell(DefaultAtlasFrame(k, rows, columns), rows, columns);
        }
    }
}
