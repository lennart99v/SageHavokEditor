using SageHavokEditor.Core.Animation;

namespace SageHavokEditor.Tools.FbxExport;

public sealed class FbxExportOptions
{
    /// <summary>Multiplies every translation. Havok units are not FBX centimetres; see the note in Program.cs.</summary>
    public float TranslationScale = 1f;
    /// <summary>Seconds between frames. Prefer the animation's own frameDuration over duration/(numFrames-1).</summary>
    public double FrameDuration = 1.0 / 30.0;
    public string TakeName = "Take 001";
    public string Creator = "SageHavokEditor hkx-fbx-export";
}

/// <summary>
/// Turns a skeleton plus a decoded clip into an FBX record tree.
///
/// No mesh, no materials, no skin cluster — this is an animation carrier, so the
/// whole scene is Model::LimbNode + NodeAttribute::LimbNode per bone, one
/// AnimationStack/Layer, and six AnimationCurves per bone hanging off two
/// AnimationCurveNodes.
///
/// Two decisions worth knowing about:
///
///  - FBX animates rotation as EULER, and Havok gives quaternions. Every frame is
///    converted with the eEulerXYZ convention (R = Rz*Ry*Rx) and then UNWRAPPED
///    against the previous frame, because the naive conversion jumps by a whole
///    turn the moment atan2 wraps, and that reads in-app as a one-frame spin.
///
///  - Every channel of every bone gets a key on every frame, and interpolation is
///    linear. The decoder already evaluates the spline per frame, so re-fitting
///    curves here would only add a second place for the animation to go wrong.
///    Model Lcl values carry the skeleton's reference pose, so the armature's
///    rest pose is the skeleton's own, not frame 0's.
/// </summary>
public static class FbxAnimationScene
{
    /// <summary>FBX time unit: KTime ticks per second (FBXSDK_TC_SECOND).</summary>
    public const long KTimeSecond = 46186158000L;

    /// <summary>Linear interpolation + generic clamp tangents — the flag word Autodesk and Blender both emit.</summary>
    private const int KeyAttrLinear = 24836;

    public static List<FbxNode> Build(Skeleton skeleton, AnimationClip clip, FbxExportOptions opt)
    {
        int nb = skeleton.ReferencePose.Length;
        if (nb == 0) throw new ArgumentException("skeleton has no bones");
        if (clip.NumFrames == 0) throw new ArgumentException("clip has no frames");

        // ── bake channels ────────────────────────────────────────────────────
        var tx = New(nb, clip.NumFrames); var ty = New(nb, clip.NumFrames); var tz = New(nb, clip.NumFrames);
        var rx = New(nb, clip.NumFrames); var ry = New(nb, clip.NumFrames); var rz = New(nb, clip.NumFrames);

        var rots = new System.Numerics.Quaternion[clip.NumFrames];
        for (int b = 0; b < nb; b++)
        {
            for (int f = 0; f < clip.NumFrames; f++)
            {
                var row = clip.Frames[f];
                var t = b < row.Length ? row[b] : skeleton.ReferencePose[b];

                tx[b][f] = (float)(t.Translation.X * opt.TranslationScale);
                ty[b][f] = (float)(t.Translation.Y * opt.TranslationScale);
                tz[b][f] = (float)(t.Translation.Z * opt.TranslationScale);

                rots[f] = t.Rotation;
            }

            var (ex, ey, ez) = BakeEulerTrack(rots);
            for (int f = 0; f < clip.NumFrames; f++)
            {
                rx[b][f] = (float)ex[f]; ry[b][f] = (float)ey[f]; rz[b][f] = (float)ez[f];
            }
        }

        // ── ids ──────────────────────────────────────────────────────────────
        long next = 1000000;
        long NextId() => next++;

        long docId = NextId();
        var modelId = new long[nb];
        var attrId = new long[nb];
        for (int b = 0; b < nb; b++) { attrId[b] = NextId(); modelId[b] = NextId(); }
        long stackId = NextId();
        long layerId = NextId();
        var cnT = new long[nb]; var cnR = new long[nb];
        var curveT = new long[nb][]; var curveR = new long[nb][];
        for (int b = 0; b < nb; b++)
        {
            cnT[b] = NextId(); curveT[b] = new[] { NextId(), NextId(), NextId() };
            cnR[b] = NextId(); curveR[b] = new[] { NextId(), NextId(), NextId() };
        }

        long stopTime = (long)Math.Round((clip.NumFrames - 1) * opt.FrameDuration * KTimeSecond);
        double fps = opt.FrameDuration > 0 ? 1.0 / opt.FrameDuration : 30.0;

        var roots = new List<FbxNode>();

        // ── header ───────────────────────────────────────────────────────────
        var head = new FbxNode("FBXHeaderExtension");
        head.Add("FBXHeaderVersion", FbxProp.I32(1003));
        head.Add("FBXVersion", FbxProp.I32((int)FbxBinarySerializer.Version));
        head.Add("Creator", FbxProp.Str(opt.Creator));
        roots.Add(head);

        var gs = new FbxNode("GlobalSettings");
        gs.Add("Version", FbxProp.I32(1000));
        var gsp = gs.Add("Properties70");
        // Skyrim's animation skeleton is Z-up, Y-forward, X-right.
        gsp.P("UpAxis", "int", "Integer", "", FbxProp.I32(2));
        gsp.P("UpAxisSign", "int", "Integer", "", FbxProp.I32(1));
        gsp.P("FrontAxis", "int", "Integer", "", FbxProp.I32(1));
        gsp.P("FrontAxisSign", "int", "Integer", "", FbxProp.I32(-1));
        gsp.P("CoordAxis", "int", "Integer", "", FbxProp.I32(0));
        gsp.P("CoordAxisSign", "int", "Integer", "", FbxProp.I32(1));
        gsp.P("OriginalUpAxis", "int", "Integer", "", FbxProp.I32(2));
        gsp.P("OriginalUpAxisSign", "int", "Integer", "", FbxProp.I32(1));
        gsp.P("UnitScaleFactor", "double", "Number", "", FbxProp.F64(1));
        gsp.P("OriginalUnitScaleFactor", "double", "Number", "", FbxProp.F64(1));
        gsp.P("TimeMode", "enum", "", "", FbxProp.I32(14));          // 14 = eCustom
        gsp.P("CustomFrameRate", "double", "Number", "", FbxProp.F64(fps));
        gsp.P("TimeSpanStart", "KTime", "Time", "", FbxProp.I64(0));
        gsp.P("TimeSpanStop", "KTime", "Time", "", FbxProp.I64(stopTime));
        roots.Add(gs);

        var docs = new FbxNode("Documents");
        docs.Add("Count", FbxProp.I32(1));
        var doc = docs.Add("Document", FbxProp.I64(docId), FbxProp.Str(""), FbxProp.Str("Scene"));
        var docp = doc.Add("Properties70");
        docp.P("SourceObject", "object", "", "");
        docp.P("ActiveAnimStackName", "KString", "", "", FbxProp.Str(opt.TakeName));
        doc.Add("RootNode", FbxProp.I64(0));
        roots.Add(docs);

        roots.Add(new FbxNode("References"));

        // ── definitions ──────────────────────────────────────────────────────
        var defs = new FbxNode("Definitions");
        defs.Add("Version", FbxProp.I32(100));
        defs.Add("Count", FbxProp.I32(1 + nb * 2 + 2 + nb * 2 + nb * 6));
        ObjType(defs, "GlobalSettings", 1);
        ObjType(defs, "NodeAttribute", nb);
        ObjType(defs, "Model", nb);
        ObjType(defs, "AnimationStack", 1);
        ObjType(defs, "AnimationLayer", 1);
        ObjType(defs, "AnimationCurveNode", nb * 2);
        ObjType(defs, "AnimationCurve", nb * 6);
        roots.Add(defs);

        // ── objects ──────────────────────────────────────────────────────────
        var objs = new FbxNode("Objects");

        for (int b = 0; b < nb; b++)
        {
            string name = b < skeleton.BoneNames.Length ? skeleton.BoneNames[b] : $"Bone{b}";
            var rest = skeleton.ReferencePose[b];
            var (ex, ey, ez) = QuatToEulerXyzDegrees(rest.Rotation);

            var attr = objs.Add("NodeAttribute",
                FbxProp.I64(attrId[b]), FbxProp.NameClass("", "NodeAttribute"), FbxProp.Str("LimbNode"));
            attr.Add("Properties70").P("Size", "double", "Number", "", FbxProp.F64(1));
            attr.Add("TypeFlags", FbxProp.Str("Skeleton"));

            var model = objs.Add("Model",
                FbxProp.I64(modelId[b]), FbxProp.NameClass(name, "Model"), FbxProp.Str("LimbNode"));
            model.Add("Version", FbxProp.I32(232));
            var mp = model.Add("Properties70");
            mp.P("RotationActive", "bool", "", "", FbxProp.I32(1));
            mp.P("InheritType", "enum", "", "", FbxProp.I32(1));
            mp.P("ScalingMax", "Vector3D", "Vector", "", FbxProp.F64(0), FbxProp.F64(0), FbxProp.F64(0));
            mp.P("DefaultAttributeIndex", "int", "Integer", "", FbxProp.I32(0));
            mp.P("Lcl Translation", "Lcl Translation", "", "A",
                FbxProp.F64(rest.Translation.X * opt.TranslationScale),
                FbxProp.F64(rest.Translation.Y * opt.TranslationScale),
                FbxProp.F64(rest.Translation.Z * opt.TranslationScale));
            mp.P("Lcl Rotation", "Lcl Rotation", "", "A", FbxProp.F64(ex), FbxProp.F64(ey), FbxProp.F64(ez));
            mp.P("Lcl Scaling", "Lcl Scaling", "", "A", FbxProp.F64(1), FbxProp.F64(1), FbxProp.F64(1));
            model.Add("Shading", FbxProp.I32(1));
            model.Add("Culling", FbxProp.Str("CullingOff"));
        }

        var stack = objs.Add("AnimationStack",
            FbxProp.I64(stackId), FbxProp.NameClass(opt.TakeName, "AnimStack"), FbxProp.Str(""));
        var sp = stack.Add("Properties70");
        sp.P("LocalStart", "KTime", "Time", "", FbxProp.I64(0));
        sp.P("LocalStop", "KTime", "Time", "", FbxProp.I64(stopTime));
        sp.P("ReferenceStart", "KTime", "Time", "", FbxProp.I64(0));
        sp.P("ReferenceStop", "KTime", "Time", "", FbxProp.I64(stopTime));

        objs.Add("AnimationLayer",
            FbxProp.I64(layerId), FbxProp.NameClass("BaseLayer", "AnimLayer"), FbxProp.Str(""));

        // key times are shared by every curve
        var keyTimes = new long[clip.NumFrames];
        for (int f = 0; f < clip.NumFrames; f++)
            keyTimes[f] = (long)Math.Round(f * opt.FrameDuration * KTimeSecond);

        for (int b = 0; b < nb; b++)
        {
            CurveNode(objs, cnT[b], "T", tx[b][0], ty[b][0], tz[b][0]);
            Curve(objs, curveT[b][0], keyTimes, tx[b]);
            Curve(objs, curveT[b][1], keyTimes, ty[b]);
            Curve(objs, curveT[b][2], keyTimes, tz[b]);

            CurveNode(objs, cnR[b], "R", rx[b][0], ry[b][0], rz[b][0]);
            Curve(objs, curveR[b][0], keyTimes, rx[b]);
            Curve(objs, curveR[b][1], keyTimes, ry[b]);
            Curve(objs, curveR[b][2], keyTimes, rz[b]);
        }
        roots.Add(objs);

        // ── connections ──────────────────────────────────────────────────────
        var conns = new FbxNode("Connections");
        for (int b = 0; b < nb; b++)
        {
            int p = b < skeleton.ParentIndices.Length ? skeleton.ParentIndices[b] : -1;
            long parent = (p >= 0 && p < nb) ? modelId[p] : 0;      // 0 = RootNode
            conns.Add("C", FbxProp.Str("OO"), FbxProp.I64(modelId[b]), FbxProp.I64(parent));
            conns.Add("C", FbxProp.Str("OO"), FbxProp.I64(attrId[b]), FbxProp.I64(modelId[b]));
        }
        conns.Add("C", FbxProp.Str("OO"), FbxProp.I64(layerId), FbxProp.I64(stackId));
        for (int b = 0; b < nb; b++)
        {
            Link(conns, cnT[b], layerId, modelId[b], "Lcl Translation", curveT[b]);
            Link(conns, cnR[b], layerId, modelId[b], "Lcl Rotation", curveR[b]);
        }
        roots.Add(conns);

        var takes = new FbxNode("Takes");
        takes.Add("Current", FbxProp.Str(opt.TakeName));
        var take = takes.Add("Take", FbxProp.Str(opt.TakeName));
        take.Add("FileName", FbxProp.Str(opt.TakeName.Replace(' ', '_') + ".tak"));
        take.Add("LocalTime", FbxProp.I64(0), FbxProp.I64(stopTime));
        take.Add("ReferenceTime", FbxProp.I64(0), FbxProp.I64(stopTime));
        roots.Add(takes);

        return roots;
    }

    // ── build helpers ────────────────────────────────────────────────────────

    private static void ObjType(FbxNode defs, string name, int count) =>
        defs.Add("ObjectType", FbxProp.Str(name)).Add("Count", FbxProp.I32(count));

    private static void CurveNode(FbxNode objs, long id, string kind, double x, double y, double z)
    {
        var n = objs.Add("AnimationCurveNode",
            FbxProp.I64(id), FbxProp.NameClass(kind, "AnimCurveNode"), FbxProp.Str(""));
        var p = n.Add("Properties70");
        p.P("d|X", "Number", "", "A", FbxProp.F64(x));
        p.P("d|Y", "Number", "", "A", FbxProp.F64(y));
        p.P("d|Z", "Number", "", "A", FbxProp.F64(z));
    }

    private static void Curve(FbxNode objs, long id, long[] keyTimes, float[] values)
    {
        var n = objs.Add("AnimationCurve",
            FbxProp.I64(id), FbxProp.NameClass("", "AnimCurve"), FbxProp.Str(""));
        n.Add("Default", FbxProp.F64(values[0]));
        n.Add("KeyVer", FbxProp.I32(4008));
        n.Add("KeyTime", FbxProp.ArrI64(keyTimes));
        n.Add("KeyValueFloat", FbxProp.ArrF32(values));
        n.Add("KeyAttrFlags", FbxProp.ArrI32(new[] { KeyAttrLinear }));
        n.Add("KeyAttrDataFloat", FbxProp.ArrI32(new[] { 0, 0, 218434821, 0 }));
        n.Add("KeyAttrRefCount", FbxProp.ArrI32(new[] { values.Length }));
    }

    private static void Link(FbxNode conns, long curveNode, long layer, long model, string prop, long[] curves)
    {
        conns.Add("C", FbxProp.Str("OO"), FbxProp.I64(curveNode), FbxProp.I64(layer));
        conns.Add("C", FbxProp.Str("OP"), FbxProp.I64(curveNode), FbxProp.I64(model), FbxProp.Str(prop));
        conns.Add("C", FbxProp.Str("OP"), FbxProp.I64(curves[0]), FbxProp.I64(curveNode), FbxProp.Str("d|X"));
        conns.Add("C", FbxProp.Str("OP"), FbxProp.I64(curves[1]), FbxProp.I64(curveNode), FbxProp.Str("d|Y"));
        conns.Add("C", FbxProp.Str("OP"), FbxProp.I64(curves[2]), FbxProp.I64(curveNode), FbxProp.Str("d|Z"));
    }

    // ── math ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// One bone's rotations to a continuous Euler track, in degrees.
    ///
    /// Every rotation has TWO XYZ Euler spellings — (x, y, z) and
    /// (x+180, 180-y, z+180) name the same orientation — and near the y = ±90
    /// singularity the naive one swings a channel by most of a turn while the
    /// bone barely moves. Twist bones sit right on that singularity, so on a real
    /// clip this is not a corner case: picking per frame whichever spelling lands
    /// closer to the previous frame, and only then unwrapping, is what keeps the
    /// curve a description of the motion rather than of the parameterisation.
    /// </summary>
    public static (double[] x, double[] y, double[] z) BakeEulerTrack(
        IReadOnlyList<System.Numerics.Quaternion> rotations)
    {
        int n = rotations.Count;
        var ox = new double[n]; var oy = new double[n]; var oz = new double[n];
        double px = 0, py = 0, pz = 0;

        for (int f = 0; f < n; f++)
        {
            var (ax, ay, az) = QuatToEulerXyzDegrees(rotations[f]);

            if (f == 0) { ox[0] = ax; oy[0] = ay; oz[0] = az; px = ax; py = ay; pz = az; continue; }

            // candidate 1: this spelling, unwrapped
            double c1x = Unwrap(ax, px), c1y = Unwrap(ay, py), c1z = Unwrap(az, pz);
            double d1 = Math.Abs(c1x - px) + Math.Abs(c1y - py) + Math.Abs(c1z - pz);

            // candidate 2: the equivalent spelling, unwrapped
            double c2x = Unwrap(ax + 180, px), c2y = Unwrap(180 - ay, py), c2z = Unwrap(az + 180, pz);
            double d2 = Math.Abs(c2x - px) + Math.Abs(c2y - py) + Math.Abs(c2z - pz);

            if (d2 < d1) { ox[f] = c2x; oy[f] = c2y; oz[f] = c2z; }
            else { ox[f] = c1x; oy[f] = c1y; oz[f] = c1z; }

            px = ox[f]; py = oy[f]; pz = oz[f];
        }

        return (ox, oy, oz);
    }

    /// <summary>
    /// Quaternion to FBX eEulerXYZ degrees. FBX composes an XYZ node as
    /// R = Rz * Ry * Rx, so the extraction below is against that matrix and not
    /// against any of the other five orderings that also answer to "XYZ".
    /// </summary>
    public static (double x, double y, double z) QuatToEulerXyzDegrees(System.Numerics.Quaternion q)
    {
        // normalise — decoded quats drift a little after repeated slerp
        double n = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (n < 1e-12) return (0, 0, 0);
        double x = q.X / n, y = q.Y / n, z = q.Z / n, w = q.W / n;

        // column-vector rotation matrix
        double r00 = 1 - 2 * (y * y + z * z);
        double r10 = 2 * (x * y + w * z);
        double r20 = 2 * (x * z - w * y);
        double r21 = 2 * (y * z + w * x);
        double r22 = 1 - 2 * (x * x + y * y);

        double ax, az;
        double ay = Math.Asin(Math.Clamp(-r20, -1.0, 1.0));

        if (Math.Abs(r20) < 0.9999995)
        {
            ax = Math.Atan2(r21, r22);
            az = Math.Atan2(r10, r00);
        }
        else
        {
            // gimbal lock: X and Z are degenerate, so fold the whole turn into Z
            double r01 = 2 * (x * y - w * z);
            double r11 = 1 - 2 * (x * x + z * z);
            ax = 0;
            az = Math.Atan2(-r01, r11);
        }

        const double ToDeg = 180.0 / Math.PI;
        return (ax * ToDeg, ay * ToDeg, az * ToDeg);
    }

    /// <summary>Shift by whole turns so consecutive frames never jump more than half a turn.</summary>
    public static double Unwrap(double value, double previous)
    {
        double d = value - previous;
        if (d > 180 || d < -180) value -= 360.0 * Math.Round(d / 360.0);
        return value;
    }

    private static float[][] New(int outer, int inner)
    {
        var a = new float[outer][];
        for (int i = 0; i < outer; i++) a[i] = new float[inner];
        return a;
    }
}
