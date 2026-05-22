using System;
using System.Numerics;

namespace SkyrimHavokEditor.Core.Animation
{
    /// <summary>
    /// Havok spline-compressed animation decompressor (TC40 quaternion + B-spline).
    /// Decode math ported verbatim from the validated standalone viewer.
    /// Operates on a SINGLE block; the parser guards numBlocks != 1.
    /// Returns [frame][track] LOCAL transforms (translation + rotation; scale = 1).
    /// The parser maps tracks → bones and fills un-animated bones from the reference pose.
    /// </summary>
    public static class HavokSplineDecoder
    {
        // decoder-local raw quat; converts to System.Numerics.Quaternion at the assembly boundary
        private struct HavokQuaternion
        {
            public float X, Y, Z, W;
            public HavokQuaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        }

        public static HkTransform[][] Decode(byte[] rawBytes, int numFrames, int maskSize)
        {
            int numTracksInAnim = maskSize / 4;

            var quatType = new byte[numTracksInAnim];
            var posType = new byte[numTracksInAnim];
            var rotType = new byte[numTracksInAnim];
            var scaleType = new byte[numTracksInAnim];
            for (int i = 0; i < numTracksInAnim; i++)
            {
                quatType[i] = rawBytes[i * 4 + 0];
                posType[i] = rawBytes[i * 4 + 1];
                rotType[i] = rawBytes[i * 4 + 2];
                scaleType[i] = rawBytes[i * 4 + 3];
            }

            // Pass 1: per-track start offsets (sequential scan from end of mask table)
            var trackOffsets = new int[numTracksInAnim];
            int scan = maskSize;
            for (int b = 0; b < numTracksInAnim; b++)
            {
                trackOffsets[b] = scan;
                SkipTrack(rawBytes, ref scan, quatType[b], posType[b], rotType[b], scaleType[b], rawBytes);
            }

            // Pass 2: decode
            var results = new (float[] px, float[] py, float[] pz, HavokQuaternion[] rot)[numTracksInAnim];
            for (int b = 0; b < numTracksInAnim; b++)
            {
                int off = trackOffsets[b];
                results[b] = DecodeTrack(rawBytes, ref off,
                    quatType[b], posType[b], rotType[b], scaleType[b], rawBytes, numFrames);
            }

            // Assemble [frame][track]
            var frames = new HkTransform[numFrames][];
            for (int f = 0; f < numFrames; f++)
            {
                var row = new HkTransform[numTracksInAnim];
                for (int b = 0; b < numTracksInAnim; b++)
                {
                    var q = results[b].rot[f];
                    row[b] = new HkTransform
                    {
                        Translation = new Vector3(results[b].px[f], results[b].py[f], results[b].pz[f]),
                        Rotation = new Quaternion(q.X, q.Y, q.Z, q.W),
                        Scale = 1f
                    };
                }
                frames[f] = row;
            }
            return frames;
        }

        // ── Skip one track (offset calculation) ───────────────────────────────
        private static void SkipTrack(byte[] d, ref int off, byte qT, byte pT, byte rT, byte sT, byte[] baseArr)
        {
            int transBpc = ((qT & 0x03) == 0) ? 1 : 2;
            int scaleBpc = (((qT >> 6) & 0x03) == 0) ? 1 : 2;

            SkipVecCurve(d, ref off, pT, transBpc, baseArr);
            Align4(ref off, baseArr);
            SkipRotation(d, ref off, rT);
            Align4(ref off, baseArr);
            SkipVecCurve(d, ref off, sT, scaleBpc, baseArr);
        }

        private static void SkipVecCurve(byte[] d, ref int off, byte mask, int bpc, byte[] baseArr)
        {
            bool anyDyn = (mask & 0xF0) != 0;
            int numCP = 0, deg = 0;

            if (anyDyn)
            {
                int n = ReadU16(d, ref off);
                deg = d[off++];
                numCP = n + 1;
                int numKnots = n + deg + 2;
                off += numKnots;
                Align4(ref off, baseArr);
            }

            int numDyn = 0;
            for (int i = 0; i < 4; i++)
            {
                if (((mask >> i) & 1) != 0) off += 4;
                if (((mask >> (i + 4)) & 1) != 0) { off += 8; numDyn++; }
            }

            if (anyDyn) off += numCP * numDyn * bpc;
        }

        private static void SkipRotation(byte[] d, ref int off, byte rT)
        {
            if (rT == 0x00) return;

            if ((rT & 0xF0) != 0)
            {
                int n = ReadU16(d, ref off);
                int deg = d[off++];
                off += n + deg + 2;     // knots
                off += (n + 1) * 5;     // ctrl pts
            }
            else
            {
                off += 5;               // one static TC40
            }
        }

        // ── Decode one track ──────────────────────────────────────────────────
        private static (float[] px, float[] py, float[] pz, HavokQuaternion[] rot)
            DecodeTrack(byte[] d, ref int off, byte qT, byte pT, byte rT, byte sT, byte[] baseArr, int numFrames)
        {
            int transBpc = ((qT & 0x03) == 0) ? 1 : 2;
            int scaleBpc = (((qT >> 6) & 0x03) == 0) ? 1 : 2;

            var (px, py, pz) = DecodeVecCurve(d, ref off, pT, transBpc, baseArr, numFrames);
            Align4(ref off, baseArr);
            var rot = DecodeRotation(d, ref off, rT, numFrames);
            Align4(ref off, baseArr);
            SkipVecCurve(d, ref off, sT, scaleBpc, baseArr);   // scale unused

            return (px, py, pz, rot);
        }

        // ── VecCurve decoder ──────────────────────────────────────────────────
        private static (float[] x, float[] y, float[] z) DecodeVecCurve(
            byte[] d, ref int off, byte mask, int bpc, byte[] baseArr, int numFrames)
        {
            bool anyDyn = (mask & 0xF0) != 0;
            int numCP = 0, deg = 0;
            byte[] knots = null;

            if (anyDyn)
            {
                int n = ReadU16(d, ref off);
                deg = d[off++];
                numCP = n + 1;
                int numKnots = n + deg + 2;
                knots = new byte[numKnots];
                Array.Copy(d, off, knots, 0, numKnots);
                off += numKnots;
                Align4(ref off, baseArr);
            }

            float[] sV = new float[4];
            float[] mn = new float[4];
            float[] mx = new float[4];
            int numDyn = 0;
            int[] dynIdx = new int[4];

            for (int i = 0; i < 4; i++)
            {
                if (((mask >> i) & 1) != 0)
                    sV[i] = ReadF32(d, ref off);

                if (((mask >> (i + 4)) & 1) != 0)
                {
                    dynIdx[i] = numDyn++;
                    mn[i] = ReadF32(d, ref off);
                    mx[i] = ReadF32(d, ref off);
                }
            }

            byte[] ctrlPts = null;
            if (anyDyn)
            {
                int cpSize = numCP * numDyn * bpc;
                ctrlPts = new byte[cpSize];
                Array.Copy(d, off, ctrlPts, 0, cpSize);
                off += cpSize;
            }

            float[] rx = new float[numFrames];
            float[] ry = new float[numFrames];
            float[] rz = new float[numFrames];

            for (int f = 0; f < numFrames; f++)
            {
                rx[f] = EvalComponent(0, f, mask, sV, mn, mx, knots, ctrlPts, numCP, deg, numDyn, dynIdx, bpc);
                ry[f] = EvalComponent(1, f, mask, sV, mn, mx, knots, ctrlPts, numCP, deg, numDyn, dynIdx, bpc);
                rz[f] = EvalComponent(2, f, mask, sV, mn, mx, knots, ctrlPts, numCP, deg, numDyn, dynIdx, bpc);
            }

            return (rx, ry, rz);
        }

        private static float EvalComponent(int axis, int fi, byte mask,
            float[] sV, float[] mn, float[] mx, byte[] knots, byte[] ctrlPts,
            int numCP, int deg, int numDyn, int[] dynIdx, int bpc)
        {
            bool isStatic = ((mask >> axis) & 1) != 0;
            bool isDynamic = ((mask >> (axis + 4)) & 1) != 0;

            if (isStatic) return sV[axis];
            if (!isDynamic) return 0f;

            int di = dynIdx[axis];
            float range = mx[axis] - mn[axis];

            int n = numCP - 1;
            int span = FindKnotSpan(knots, fi, n, deg);

            float[] dArr = new float[deg + 1];
            for (int j = 0; j <= deg; j++)
            {
                int ci = Math.Clamp(span - deg + j, 0, n);
                dArr[j] = UnpackFloat(ctrlPts, ci, di, numDyn, bpc, mn[axis], range);
            }

            for (int r = 1; r <= deg; r++)
            {
                for (int j = deg; j >= r; j--)
                {
                    float klo = knots[j + span - deg];
                    float khi = knots[j + span - r + 1];
                    float a = (khi > klo) ? Math.Clamp((fi - klo) / (khi - klo), 0f, 1f) : 0f;
                    dArr[j] = (1f - a) * dArr[j - 1] + a * dArr[j];
                }
            }
            return dArr[deg];
        }

        private static float UnpackFloat(byte[] ctrlPts, int cpIdx, int dynComp,
            int numDyn, int bpc, float mn, float range)
        {
            int byteIdx = (cpIdx * numDyn + dynComp) * bpc;
            if (bpc == 1)
                return mn + (ctrlPts[byteIdx] / 255f) * range;
            ushort v = (ushort)(ctrlPts[byteIdx] | (ctrlPts[byteIdx + 1] << 8));
            return mn + (v / 65535f) * range;
        }

        // ── Rotation decoder ──────────────────────────────────────────────────
        private static HavokQuaternion[] DecodeRotation(byte[] d, ref int off, byte rT, int numFrames)
        {
            var result = new HavokQuaternion[numFrames];

            if (rT == 0x00)
            {
                var id = new HavokQuaternion(0, 0, 0, 1);
                for (int i = 0; i < numFrames; i++) result[i] = id;
                return result;
            }

            if ((rT & 0xF0) == 0)
            {
                var q = DecodeTC40(d, off); off += 5;
                for (int i = 0; i < numFrames; i++) result[i] = q;
                return result;
            }

            int n = ReadU16(d, ref off);
            int deg = d[off++];
            int numCP = n + 1;
            int numKnots = n + deg + 2;

            byte[] knots = new byte[numKnots];
            Array.Copy(d, off, knots, 0, numKnots);
            off += numKnots;

            var cp = new HavokQuaternion[numCP];
            for (int i = 0; i < numCP; i++) { cp[i] = DecodeTC40(d, off); off += 5; }

            for (int f = 0; f < numFrames; f++)
            {
                int fi = f;
                int span = FindKnotSpan(knots, fi, n, deg);

                var dArr = new HavokQuaternion[deg + 1];
                for (int j = 0; j <= deg; j++)
                {
                    int ci = Math.Clamp(span - deg + j, 0, n);
                    dArr[j] = cp[ci];
                }

                for (int r = 1; r <= deg; r++)
                {
                    for (int j = deg; j >= r; j--)
                    {
                        float klo = knots[j + span - deg];
                        float khi = knots[j + span - r + 1];
                        float a = (khi > klo) ? Math.Clamp((fi - klo) / (khi - klo), 0f, 1f) : 0f;
                        float dot = dArr[j - 1].X * dArr[j].X + dArr[j - 1].Y * dArr[j].Y
                                  + dArr[j - 1].Z * dArr[j].Z + dArr[j - 1].W * dArr[j].W;
                        if (dot < 0f) dArr[j] = new HavokQuaternion(-dArr[j].X, -dArr[j].Y, -dArr[j].Z, -dArr[j].W);
                        dArr[j] = Slerp(dArr[j - 1], dArr[j], a);
                    }
                }
                result[f] = dArr[deg];
            }
            return result;
        }

        // ── ThreeComp40 ───────────────────────────────────────────────────────
        private static HavokQuaternion DecodeTC40(byte[] b, int offset)
        {
            uint Va = b[offset] | ((uint)(b[offset + 1] & 0xF) << 8);
            uint Vb = (uint)((b[offset + 1] >> 4) & 0xF) | ((uint)b[offset + 2] << 4);
            uint Vc = b[offset + 3] | ((uint)(b[offset + 4] & 0xF) << 8);
            int rs = (b[offset + 4] >> 4) & 0x3;
            bool sign = ((b[offset + 4] >> 6) & 0x1) != 0;

            const float kInvSqrt2 = 0.70710678118f;
            float Dq(uint q) => (q / 4095f) * (2f * kInvSqrt2) - kInvSqrt2;

            float[] s = { Dq(Va), Dq(Vb), Dq(Vc) };
            float sumSq = s[0] * s[0] + s[1] * s[1] + s[2] * s[2];
            float dd = (float)Math.Sqrt(Math.Max(0f, 1f - sumSq));
            if (sign) dd = -dd;

            float[] c = new float[4];
            c[rs] = dd;
            int si = 0;
            for (int i = 0; i < 4; i++) if (i != rs) c[i] = s[si++];

            return new HavokQuaternion(c[0], c[1], c[2], c[3]);
        }

        private static int FindKnotSpan(byte[] knots, int fi, int n, int deg)
        {
            if (fi >= knots[n + 1]) return n;
            if (fi <= knots[0]) return deg;
            int lo = deg, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (knots[mid] <= fi) lo = mid; else hi = mid - 1;
            }
            return Math.Clamp(lo, deg, n);
        }

        private static HavokQuaternion Slerp(HavokQuaternion a, HavokQuaternion b, float t)
        {
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            dot = Math.Clamp(dot, -1f, 1f);
            float theta = (float)Math.Acos(Math.Abs(dot));
            if (theta < 1e-6f)
            {
                float rx = a.X + t * (b.X - a.X);
                float ry = a.Y + t * (b.Y - a.Y);
                float rz = a.Z + t * (b.Z - a.Z);
                float rw = a.W + t * (b.W - a.W);
                float len = (float)Math.Sqrt(rx * rx + ry * ry + rz * rz + rw * rw);
                return len > 0 ? new HavokQuaternion(rx / len, ry / len, rz / len, rw / len)
                               : new HavokQuaternion(0, 0, 0, 1);
            }
            float sinT = (float)Math.Sin(theta);
            float wa = (float)Math.Sin((1f - t) * theta) / sinT;
            float wb = (float)Math.Sin(t * theta) / sinT;
            if (dot < 0f) wb = -wb;
            return new HavokQuaternion(
                wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z, wa * a.W + wb * b.W);
        }

        private static ushort ReadU16(byte[] d, ref int off) { ushort v = (ushort)(d[off] | (d[off + 1] << 8)); off += 2; return v; }
        private static float ReadF32(byte[] d, ref int off) { float v = BitConverter.ToSingle(d, off); off += 4; return v; }
        private static void Align4(ref int off, byte[] baseArr) { int rem = off % 4; if (rem != 0) off += 4 - rem; }
    }
}