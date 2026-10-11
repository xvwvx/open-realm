using System;
using MdxPreview.Core.Math3D;

namespace MdxPreview.Core.Mdx
{
    /// <summary>
    /// Everything a track sample needs: the authored frame time, the sequence
    /// interval the frame belongs to, and the monotonic clock that drives global
    /// sequences. Mirrors the state the C engine reads from <c>tr.viewDef</c> and
    /// the entity frame.
    /// </summary>
    public struct MdxSampleContext
    {
        /// <summary>Authored frame time (milliseconds) inside the model timeline.</summary>
        public float Frame;
        public int SequenceStart;
        public int SequenceEnd;
        public bool HasSequence;
        /// <summary>Monotonic time in milliseconds, used by global-sequence tracks.</summary>
        public float GlobalTimeMs;
    }

    /// <summary>
    /// Keyframe track evaluation — pure C# port of
    /// games/warcraft-3/renderer/mdx/r_mdx_interpolation.c and
    /// MDLX_GetModelKeytrackValue() from r_mdx_anim.c, with fractional frame
    /// times so the preview plays smoothly instead of stepping on integer frames.
    /// </summary>
    public static class MdxTrackSampler
    {
        public static float Lerp(float a, float b, float t) => a * (1f - t) + b * t;

        /// <summary>Engine bezier basis (r_mdx_interpolation.c: bezier).</summary>
        public static float Bezier(float left, float outTan, float inTan, float right, float t)
        {
            float inverseFactor = 1f - t;
            float inverseFactorTimesTwo = inverseFactor * inverseFactor;
            float factorTimes2 = t * t;
            float factor1 = inverseFactorTimesTwo * inverseFactor;
            float factor2 = 3f * t * inverseFactorTimesTwo;
            float factor3 = 3f * factorTimes2 * inverseFactor;
            float factor4 = factorTimes2 * t;
            return left * factor1 + outTan * factor2 + inTan * factor3 + right * factor4;
        }

        /// <summary>Engine hermite basis (r_mdx_interpolation.c: hermite).</summary>
        public static float Hermite(float left, float outTan, float inTan, float right, float t)
        {
            float factorTimes2 = t * t;
            float factor1 = factorTimes2 * (2f * t - 3f) + 1f;
            float factor2 = factorTimes2 * (t - 2f) + t;
            float factor3 = factorTimes2 * (t - 1f);
            float factor4 = factorTimes2 * (3f - 2f * t);
            return left * factor1 + outTan * factor2 + inTan * factor3 + right * factor4;
        }

        /// <summary>Engine Quaternion_sqlerp: slerp(slerp(a,d,t), slerp(b,c,t), 2t(1-t)).</summary>
        public static Quat SqLerp(Quat a, Quat b, Quat c, Quat d, float t)
        {
            Quat temp1 = Quat.Slerp(a, d, t);
            Quat temp2 = Quat.Slerp(b, c, t);
            return Quat.Slerp(temp1, temp2, 2f * t * (1f - t));
        }

        /// <summary>
        /// Samples a track into <paramref name="output"/> (length &gt;= track.Components).
        /// Returns false when the track contributes nothing (no keys, no keys inside
        /// the sequence interval, or an invalid global sequence), in which case the
        /// caller keeps its default value — exactly like MDLX_GetModelKeytrackValue.
        /// </summary>
        public static bool TrySample(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, float[] output)
        {
            if (model == null || track == null || output == null || track.KeyCount == 0) return false;

            int components = track.Components;
            int keyCount = track.KeyCount;

            int intervalStart;
            int intervalEnd;
            float time = context.Frame;

            if (track.GlobalSequenceId >= 0)
            {
                if (track.GlobalSequenceId >= model.GlobalSequences.Count) return false;
                intervalStart = 0;
                intervalEnd = (int)model.GlobalSequences[track.GlobalSequenceId];

                // A global sequence whose first authored key lies beyond the declared
                // duration is a constant equal to that first key (Warsmash behaviour,
                // used by DNC light-node rotation).
                if (track.Frames[0] > intervalEnd)
                {
                    for (int c = 0; c < components; c++) output[c] = track.GetComponent(0, c);
                    return true;
                }

                uint globalLength = (uint)(intervalEnd + 1);
                float globalMs = context.GlobalTimeMs;
                time = globalLength > 0 ? globalMs % globalLength : 0f;
            }
            else
            {
                if (!context.HasSequence) return false;
                intervalStart = context.SequenceStart;
                intervalEnd = context.SequenceEnd;
            }

            int firstIndex = LowerBound(track, intervalStart);
            int endIndex = UpperBound(track, intervalEnd);
            if (firstIndex >= endIndex) return false;

            int lastIndex = endIndex - 1;

            if (time >= track.Frames[lastIndex])
            {
                // The interval tail blends back to its first key; keys outside this
                // sequence never participate.
                int span = (intervalEnd - track.Frames[lastIndex]) + (track.Frames[firstIndex] - intervalStart);
                if (firstIndex != lastIndex && span > 0 && time < intervalEnd)
                {
                    float t = (time - track.Frames[lastIndex]) / span;
                    EvaluateKeyPair(track, lastIndex, firstIndex, t, output);
                }
                else
                {
                    for (int c = 0; c < components; c++) output[c] = track.GetComponent(lastIndex, c);
                }
                return true;
            }

            int rightIndex = LowerBound(track, time);
            if (rightIndex >= keyCount) rightIndex = keyCount - 1;

            if (rightIndex == firstIndex || track.Frames[rightIndex] == time)
            {
                for (int c = 0; c < components; c++) output[c] = track.GetComponent(rightIndex, c);
                return true;
            }

            int leftIndex = rightIndex - 1;
            int leftTime = track.Frames[leftIndex];
            int rightTime = track.Frames[rightIndex];
            float blend = rightTime == leftTime ? 0f : (time - leftTime) / (rightTime - leftTime);
            EvaluateKeyPair(track, leftIndex, rightIndex, blend, output);
            return true;
        }

        static void EvaluateKeyPair(MdxKeyTrack track, int leftIndex, int rightIndex, float t, float[] output)
        {
            int components = track.Components;
            for (int c = 0; c < components; c++)
            {
                float left = track.GetComponent(leftIndex, c);
                float right = track.GetComponent(rightIndex, c);
                output[c] = track.Interpolation switch
                {
                    MdxTrackInterpolation.None => left,
                    MdxTrackInterpolation.Linear => Lerp(left, right, t),
                    MdxTrackInterpolation.Hermite => Hermite(
                        left, OutTangent(track, leftIndex, c), InTangent(track, rightIndex, c), right, t),
                    MdxTrackInterpolation.Bezier => Bezier(
                        left, OutTangent(track, leftIndex, c), InTangent(track, rightIndex, c), right, t),
                    _ => left,
                };
            }

            if (track.DataType == MdxTrackData.Float4)
            {
                // Quaternions use the squad-style blend from the engine reference.
                if (track.Interpolation is MdxTrackInterpolation.Hermite or MdxTrackInterpolation.Bezier)
                {
                    Quat a = ReadQuat(track, leftIndex);
                    Quat b = ReadQuat(track, leftIndex, tangent: true, inTangent: false);
                    Quat c = ReadQuat(track, rightIndex, tangent: true, inTangent: true);
                    Quat d = ReadQuat(track, rightIndex);
                    Quat q = SqLerp(a, b, c, d, t);
                    output[0] = q.X; output[1] = q.Y; output[2] = q.Z; output[3] = q.W;
                }
                else if (track.Interpolation == MdxTrackInterpolation.Linear)
                {
                    Quat q = Quat.Slerp(ReadQuat(track, leftIndex), ReadQuat(track, rightIndex), t);
                    output[0] = q.X; output[1] = q.Y; output[2] = q.Z; output[3] = q.W;
                }
            }
        }

        static float InTangent(MdxKeyTrack track, int keyIndex, int component)
        {
            float[] tan = track.InTangents;
            if (tan == null) return 0f;
            int components = track.Components;
            int index = keyIndex * components + component;
            return index < tan.Length ? tan[index] : 0f;
        }

        static float OutTangent(MdxKeyTrack track, int keyIndex, int component)
        {
            float[] tan = track.OutTangents;
            if (tan == null) return 0f;
            int components = track.Components;
            int index = keyIndex * components + component;
            return index < tan.Length ? tan[index] : 0f;
        }

        static Quat ReadQuat(MdxKeyTrack track, int keyIndex, bool tangent = false, bool inTangent = false)
        {
            float[] source = tangent ? (inTangent ? track.InTangents : track.OutTangents) : null;
            int baseIndex = keyIndex * 4;
            if (source == null)
                return new Quat(track.Values[baseIndex], track.Values[baseIndex + 1], track.Values[baseIndex + 2], track.Values[baseIndex + 3]);
            if (baseIndex + 3 >= source.Length) return Quat.Identity;
            return new Quat(source[baseIndex], source[baseIndex + 1], source[baseIndex + 2], source[baseIndex + 3]);
        }

        /// <summary>First key index whose frame is &gt;= <paramref name="time"/>.</summary>
        static int LowerBound(MdxKeyTrack track, float time)
        {
            int lo = 0, hi = track.KeyCount;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (track.Frames[mid] < time) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>First key index whose frame is &gt; <paramref name="time"/>.</summary>
        static int UpperBound(MdxKeyTrack track, float time)
        {
            int lo = 0, hi = track.KeyCount;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (track.Frames[mid] <= time) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        // ---- typed convenience wrappers -------------------------------------------------

        [ThreadStatic] static float[] _scratch;

        static float[] Scratch => _scratch ??= new float[4];

        public static float SampleFloat(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, float fallback)
            => TrySample(model, track, context, Scratch) ? Scratch[0] : fallback;

        public static int SampleInt(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, int fallback)
            => TrySample(model, track, context, Scratch) ? (int)Scratch[0] : fallback;

        public static Vec3 SampleVec3(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, Vec3 fallback)
            => TrySample(model, track, context, Scratch)
                ? new Vec3(Scratch[0], Scratch[1], Scratch[2])
                : fallback;

        public static Quat SampleQuat(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, Quat fallback)
            => TrySample(model, track, context, Scratch)
                ? new Quat(Scratch[0], Scratch[1], Scratch[2], Scratch[3])
                : fallback;

        /// <summary>
        /// Samples an animated colour track and applies the file's BGR component
        /// semantics (MDLX_GetAnimatedColorTrackValue).
        /// </summary>
        public static Vec3 SampleColor(MdxModel model, MdxKeyTrack track, in MdxSampleContext context, Vec3 fallback)
        {
            Vec3 value = SampleVec3(model, track, context, fallback);
            return new Vec3(value.Z, value.Y, value.X);
        }

        /// <summary>Swizzles a static authored colour exactly like MDLX_GetGeosetAnimationStaticColor.</summary>
        public static Vec3 SwizzleAuthoredColor(Vec3 color) => new Vec3(color.Z, color.Y, color.X);
    }
}
