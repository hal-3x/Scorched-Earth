using Unity.Mathematics;

namespace ScorchedEarth.Geometry
{
    /// <summary>
    /// A fire front expressed as a line segment with a width.
    ///
    /// Real fires present as a front, not a point: a burning row of houses or a
    /// wildfire edge reads as a continuous line of flame. Vanilla attaches a fixed
    /// number of flame effects at fixed offsets on the object, which looks like a
    /// blob. Here the burning object's own footprint decides the line: the segment
    /// runs along the object's longest horizontal axis, and <see cref="m_Width"/>
    /// comes from the perpendicular axis so a wide warehouse burns as a wide front
    /// and a lamppost burns as a point.
    /// </summary>
    public struct FireLine
    {
        /// <summary>Segment start, world space.</summary>
        public float3 m_Start;

        /// <summary>Segment end, world space.</summary>
        public float3 m_End;

        /// <summary>Front thickness in metres, perpendicular to the segment.</summary>
        public float m_Width;

        public FireLine(float3 start, float3 end, float width)
        {
            m_Start = start;
            m_End = end;
            m_Width = width;
        }

        public float Length => math.distance(m_Start.xz, m_End.xz);

        /// <summary>Unit direction on the XZ plane; falls back to +X for a degenerate line.</summary>
        public float2 Direction
        {
            get
            {
                float2 d = m_End.xz - m_Start.xz;
                float len = math.length(d);
                return len < 1e-4f ? new float2(1f, 0f) : d / len;
            }
        }

        /// <summary>Point at normalised position <paramref name="t"/> along the segment.</summary>
        public float3 Sample(float t)
        {
            return math.lerp(m_Start, m_End, math.saturate(t));
        }

        /// <summary>
        /// Builds the fire line from an object's oriented footprint.
        /// <paramref name="size"/> is the prefab's local X/Y/Z extent; the longer of X
        /// and Z becomes the line axis and the shorter becomes the width.
        /// </summary>
        /// <param name="position">Object world position.</param>
        /// <param name="rotation">Object world rotation.</param>
        /// <param name="size">Prefab local size (X = width, Y = height, Z = depth).</param>
        /// <param name="coverage">Fraction of the footprint the front spans (0..1).</param>
        public static FireLine FromFootprint(float3 position, quaternion rotation, float3 size, float coverage)
        {
            float sizeX = math.max(0f, size.x);
            float sizeZ = math.max(0f, size.z);

            bool alongX = sizeX >= sizeZ;
            float longSide = alongX ? sizeX : sizeZ;
            float shortSide = alongX ? sizeZ : sizeX;

            // Local axis of the longest side, rotated into world space.
            float3 localAxis = alongX ? new float3(1f, 0f, 0f) : new float3(0f, 0f, 1f);
            float3 axis = math.rotate(rotation, localAxis);
            axis.y = 0f;

            float axisLen = math.length(axis);
            axis = axisLen < 1e-4f ? new float3(1f, 0f, 0f) : axis / axisLen;

            float half = 0.5f * longSide * math.saturate(coverage);
            return new FireLine(position - axis * half, position + axis * half, shortSide * math.saturate(coverage));
        }

        /// <summary>
        /// How many flame sprites the line needs. Sprites are spaced by
        /// <paramref name="spacing"/> so the front stays visually continuous, then the
        /// count is clamped to <paramref name="maxSprites"/> to bound the cost.
        /// </summary>
        public int SpriteCount(float spacing, int maxSprites)
        {
            if (spacing <= 1e-3f) return 1;
            int needed = (int)math.ceil(Length / spacing) + 1;
            return math.clamp(needed, 1, maxSprites);
        }

        /// <summary>
        /// Sprite scale that keeps the front continuous when <paramref name="count"/>
        /// sprites are spread over the line. When the count is clamped by the budget the
        /// sprites are stretched to close the gaps, so a long front stays a solid line of
        /// flame rather than becoming a dotted one.
        /// </summary>
        public float SpriteSpan(int count)
        {
            if (count <= 1) return math.max(m_Width, Length);
            return Length / (count - 1);
        }

        /// <summary>Position of sprite <paramref name="index"/> of <paramref name="count"/> on the line.</summary>
        public float3 SpritePosition(int index, int count)
        {
            if (count <= 1) return Sample(0.5f);
            return Sample(index / (float)(count - 1));
        }

        /// <summary>
        /// Lateral offset used to give the front thickness. Alternating sides keeps the
        /// line from looking like a single flat ribbon without needing extra sprites.
        /// </summary>
        public float3 LateralOffset(int index, float amount)
        {
            float2 dir = Direction;
            float2 normal = new float2(-dir.y, dir.x);
            float side = ((index & 1) == 0) ? 1f : -1f;
            float2 offset = normal * (side * amount);
            return new float3(offset.x, 0f, offset.y);
        }
    }
}
