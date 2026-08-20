using Unity.Mathematics;

namespace ScorchedEarth.Geometry
{
    /// <summary>
    /// Turns a smouldering <see cref="Circle"/> into a small set of scaled smoke
    /// sprites that cover it.
    ///
    /// The vanilla game attaches one smoke effect per burning object, so a block of
    /// twenty burned buildings costs twenty emitters. Here the cost is decoupled from
    /// the object count: a circle of any size is covered by at most
    /// <c>maxSprites</c> sprites whose radius is solved from the area equation
    ///
    ///     n * pi * r^2 = k * pi * R^2   =>   r = R * sqrt(k / n)
    ///
    /// where <c>k</c> is an overlap factor (&gt;1 so neighbouring puffs blend instead
    /// of leaving gaps). Sprite centres use a Vogel (sunflower) spiral, which spreads
    /// points evenly over a disc without the visible banding of concentric rings.
    /// </summary>
    public static class SmokeLayout
    {
        /// <summary>Golden angle in radians - the Vogel spiral's angular step.</summary>
        public const float GoldenAngle = 2.39996323f;

        /// <summary>
        /// Number of sprites needed for a circle. Grows with the square root of the
        /// area (i.e. linearly with radius) so cost stays sub-linear in burned area.
        /// </summary>
        public static int SpriteCount(float radius, float spriteRadius, int maxSprites)
        {
            if (radius <= 0f || spriteRadius <= 0f) return 0;
            // Area-based estimate, then clamped to the caller's budget.
            float estimate = (radius * radius) / (spriteRadius * spriteRadius);
            return math.clamp((int)math.ceil(estimate), 1, maxSprites);
        }

        /// <summary>
        /// Per-sprite radius that makes <paramref name="count"/> sprites cover a circle
        /// of <paramref name="radius"/> with the given overlap factor.
        /// </summary>
        public static float SpriteRadius(float radius, int count, float overlap)
        {
            if (count <= 0) return 0f;
            return radius * math.sqrt(overlap / count);
        }

        /// <summary>
        /// Position of sprite <paramref name="index"/> of <paramref name="count"/> on a
        /// Vogel spiral inside the circle. <paramref name="jitterSeed"/> rotates the whole
        /// spiral so neighbouring areas do not line up identically.
        /// </summary>
        public static float2 SpritePosition(in Circle circle, int index, int count, float spriteRadius, uint jitterSeed)
        {
            if (count <= 1) return circle.m_Center;

            // Keep sprite discs inside the area: the outermost centre sits one sprite
            // radius short of the rim, so coverage ends at the circle boundary.
            float usable = math.max(0f, circle.m_Radius - spriteRadius * 0.5f);
            float t = (index + 0.5f) / count;
            float r = usable * math.sqrt(t);

            var random = Unity.Mathematics.Random.CreateFromIndex(jitterSeed);
            float phase = random.NextFloat(0f, 2f * math.PI);
            float angle = index * GoldenAngle + phase;

            math.sincos(angle, out float s, out float c);
            return circle.m_Center + new float2(c, s) * r;
        }

        /// <summary>
        /// Intensity falloff toward the rim so the area fades out instead of ending
        /// with a hard edge. Centre sprites are strongest.
        /// </summary>
        public static float SpriteIntensity(int index, int count, float centreBias)
        {
            if (count <= 1) return 1f;
            float t = (index + 0.5f) / count;     // 0 at centre, 1 at rim
            return math.lerp(1f, centreBias, t);
        }
    }
}
