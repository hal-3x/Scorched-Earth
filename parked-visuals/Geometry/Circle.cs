using Unity.Mathematics;

namespace ScorchedEarth.Geometry
{
    /// <summary>
    /// A horizontal (XZ) circle with a representative ground height.
    /// Smoldering areas are modelled as circles so that a whole cluster of burned
    /// objects can be covered by a handful of large smoke sprites instead of one
    /// sprite per object.
    /// </summary>
    public struct Circle
    {
        /// <summary>Centre on the XZ plane.</summary>
        public float2 m_Center;

        /// <summary>Radius in metres.</summary>
        public float m_Radius;

        /// <summary>Representative terrain/base height for the covered area.</summary>
        public float m_Height;

        public Circle(float2 center, float radius, float height)
        {
            m_Center = center;
            m_Radius = radius;
            m_Height = height;
        }

        public float Area => math.PI * m_Radius * m_Radius;

        public bool Contains(float2 point)
        {
            return math.lengthsq(point - m_Center) <= m_Radius * m_Radius;
        }

        /// <summary>Squared distance from the circle centre, for cheap comparisons.</summary>
        public float DistanceSqTo(float2 point)
        {
            return math.lengthsq(point - m_Center);
        }

        /// <summary>
        /// Grows the circle just enough to contain <paramref name="point"/> plus
        /// <paramref name="padding"/>. The centre is shifted toward the point so the
        /// circle stays as tight as possible instead of ballooning around a fixed centre.
        /// </summary>
        public void Encapsulate(float2 point, float height, float padding)
        {
            float2 delta = point - m_Center;
            float dist = math.length(delta);
            float needed = dist + padding;
            if (needed <= m_Radius)
            {
                // Already covered - only blend the height so the area tracks the terrain.
                m_Height = math.lerp(m_Height, height, 0.15f);
                return;
            }

            if (dist < 1e-4f)
            {
                m_Radius = math.max(m_Radius, padding);
            }
            else
            {
                // Minimal enclosing circle of (this circle) and (point + padding):
                // new radius is half the span, new centre sits on the connecting line.
                float newRadius = 0.5f * (m_Radius + needed);
                float2 dir = delta / dist;
                m_Center += dir * (newRadius - m_Radius);
                m_Radius = newRadius;
            }

            m_Height = math.lerp(m_Height, height, 0.5f);
        }

        /// <summary>Minimal circle enclosing both inputs.</summary>
        public static Circle Union(Circle a, Circle b)
        {
            float2 delta = b.m_Center - a.m_Center;
            float dist = math.length(delta);

            if (dist + b.m_Radius <= a.m_Radius) return a;
            if (dist + a.m_Radius <= b.m_Radius) return b;

            float radius = 0.5f * (dist + a.m_Radius + b.m_Radius);
            float2 center = dist < 1e-4f
                ? a.m_Center
                : a.m_Center + (delta / dist) * (radius - a.m_Radius);

            return new Circle(center, radius, 0.5f * (a.m_Height + b.m_Height));
        }

        /// <summary>True when the two circles overlap by at least <paramref name="slack"/> metres.</summary>
        public static bool Overlaps(Circle a, Circle b, float slack)
        {
            float r = a.m_Radius + b.m_Radius + slack;
            return math.lengthsq(b.m_Center - a.m_Center) <= r * r;
        }
    }
}
