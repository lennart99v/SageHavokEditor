using System.Numerics;

namespace SageHavokEditor.Core.Animation
{
    /// <summary>Local or world bone transform. Column-major composition (parent * local).</summary>
    public struct HkTransform
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 ScaleVector;
        // Compatibility with the spline decoder and existing uniform-scale callers.
        public float Scale
        {
            readonly get => ScaleVector.X;
            set => ScaleVector = new Vector3(value);
        }

        public static readonly HkTransform Identity = new()
        {
            Translation = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = 1f
        };

        // world = parent * local  — matches hkaBone.GetWorldCoordinate composition order
        public static HkTransform operator *(HkTransform a, HkTransform b) => new()
        {
            Translation = a.Translation + Vector3.Transform(b.Translation * a.ScaleVector, a.Rotation),
            Rotation = a.Rotation * b.Rotation,
            ScaleVector = a.ScaleVector * b.ScaleVector
        };

        public static HkTransform[] ComputeWorld(HkTransform[] local, int[] parentIndices)
        {
            var world = new HkTransform[local.Length];
            for (int i = 0; i < local.Length; i++)
            {
                int p = i < parentIndices.Length ? parentIndices[i] : -1;
                world[i] = (p < 0 || p >= i) ? local[i] : world[p] * local[i];
            }
            return world;
        }
    }
}
