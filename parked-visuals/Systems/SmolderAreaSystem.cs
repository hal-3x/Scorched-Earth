using System.Collections.Generic;
using Colossal.Serialization.Entities;
using ScorchedEarth.Geometry;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Transform = Game.Objects.Transform;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Covers recently burned ground with a few large smoke plumes sized to the area,
    /// rather than one plume per object.
    ///
    /// <para>The burned objects in a neighbourhood are clustered into circles. Each circle
    /// is then filled with smoke sprites whose radius is solved from the area equation in
    /// <see cref="SmokeLayout"/>, so the sprite count grows with the <em>radius</em> of the
    /// burned area rather than with the number of objects in it. Twenty burned buildings on
    /// one block cost about the same as three, which is the whole point: in a real fire the
    /// smoke is a property of the area, not of each individual structure.</para>
    ///
    /// <para>This only ever draws the fire-smoke effects identified by
    /// <see cref="FireEffectCatalogSystem"/>. Industrial smoke, steam and water vapour are
    /// never touched, so smokestacks keep behaving exactly as they do in vanilla.</para>
    /// </summary>
    public sealed partial class SmolderAreaSystem : GameSystemBase
    {
        /// <summary>Roughly how many metres one smoke effect covers at scale 1, by rank.</summary>
        private static readonly float[] kSmokeNominalSize = { 10f, 10f };

        private const float kMinScale = 0.4f;
        private const float kMaxScale = 6f;

        /// <summary>Share of the city-wide sprite budget reserved for smoke.</summary>
        private const float kSmokeBudgetShare = 0.35f;

        /// <summary>Char level below which an object has stopped smouldering.</summary>
        private const float kSmolderThreshold = 0.08f;

        /// <summary>Upper bound on tracked areas, so a city-wide disaster stays affordable.</summary>
        private const int kMaxAreas = 64;

        /// <summary>How far sprite discs are allowed to overlap. Above 1 they blend.</summary>
        private const float kOverlap = 1.35f;

        /// <summary>Intensity of the outermost sprites relative to the centre.</summary>
        private const float kRimFalloff = 0.35f;

        /// <summary>Height above the ground that plumes are anchored at.</summary>
        private const float kPlumeHeight = 3f;

        private struct SmolderSource
        {
            public float3 m_Position;
            public float m_Strength;
        }

        private struct SmolderArea
        {
            public Circle m_Circle;
            public float m_Strength;
            public int m_Count;
        }

        /// <summary>Fastest tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 16;

        /// <summary>Rebuilds are this many times rarer than the flame rebuild.</summary>
        private const int kIntervalMultiplier = 4;

        private EntityQuery m_CharredQuery;
        private EntityQuery m_DestroyedQuery;
        private SimulationSystem m_SimulationSystem;
        private UpdateThrottle m_Throttle;

        private FireEffectCatalogSystem m_Catalog;
        private CameraUpdateSystem m_CameraSystem;
        private EffectSpritePool m_Pool;
        private EndFrameBarrier m_Barrier;

        private readonly List<SmolderSource> m_Sources = new List<SmolderSource>();
        private readonly List<SmolderArea> m_Areas = new List<SmolderArea>();

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Catalog = World.GetOrCreateSystemManaged<FireEffectCatalogSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_Pool = new EffectSpritePool(EntityManager, EffectRole.Smoke);

            m_CharredQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Charred>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Rubble smoulders until it is cleared away.
            m_DestroyedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Destroyed>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        /// <summary>
        /// Smouldering areas move slowly, so they are rebuilt far less often than flames.
        /// The user's interval is applied on top of this in OnUpdate.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kBaseInterval;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;

            if (settings == null || !settings.SmolderAreas || !m_Catalog.Ready || m_Catalog.Smokes.Count == 0)
            {
                if (m_Pool.PooledCount > 0)
                {
                    m_Pool.Begin(m_Barrier.CreateCommandBuffer());
                    m_Pool.End();
                }

                return;
            }

            uint elapsed;
            if (!m_Throttle.ShouldRun(
                    m_SimulationSystem.frameIndex, settings.SafeUpdateInterval * kIntervalMultiplier, out elapsed))
            {
                return;
            }

            CollectSources();
            BuildAreas(settings);

            m_Pool.Begin(m_Barrier.CreateCommandBuffer());

            int budget = ComputeBudget(settings);
            for (int i = 0; i < m_Areas.Count && budget > 0; i++)
            {
                budget -= EmitArea(m_Areas[i], settings, budget, i);
            }

            m_Pool.End();

            Mod.Verbose(() => "Smouldering: " + m_Sources.Count + " source(s) in " + m_Areas.Count
                            + " area(s), " + m_Pool.ActiveCount + " smoke sprite(s).");
        }

        /// <summary>Gathers everything currently giving off smoke: charred objects and rubble.</summary>
        private void CollectSources()
        {
            m_Sources.Clear();

            NativeArray<Entity> charred = m_CharredQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < charred.Length; i++)
                {
                    Charred state = EntityManager.GetComponentData<Charred>(charred[i]);
                    if (state.m_Amount < kSmolderThreshold)
                    {
                        continue;
                    }

                    Transform transform = EntityManager.GetComponentData<Transform>(charred[i]);
                    m_Sources.Add(new SmolderSource
                    {
                        m_Position = transform.m_Position,

                        // Smoke fades out with the soot, so an area stops smoking on its own.
                        m_Strength = state.m_Amount,
                    });
                }
            }
            finally
            {
                charred.Dispose();
            }

            NativeArray<Entity> destroyed = m_DestroyedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < destroyed.Length; i++)
                {
                    Destroyed state = EntityManager.GetComponentData<Destroyed>(destroyed[i]);
                    float remaining = 1f - math.saturate(state.m_Cleared);
                    if (remaining < kSmolderThreshold)
                    {
                        continue;
                    }

                    Transform transform = EntityManager.GetComponentData<Transform>(destroyed[i]);
                    m_Sources.Add(new SmolderSource
                    {
                        m_Position = transform.m_Position,
                        m_Strength = remaining,
                    });
                }
            }
            finally
            {
                destroyed.Dispose();
            }
        }

        /// <summary>
        /// Greedy single-pass clustering: each source joins the first area within range,
        /// growing it just enough to contain the source, or starts a new one.
        ///
        /// This is deliberately not an optimal clustering. It is O(sources * areas) with a
        /// hard cap on areas, runs on a slow tick, and the result only has to look right -
        /// a slightly larger circle costs nothing extra because the sprite count is solved
        /// from the radius either way.
        /// </summary>
        private void BuildAreas(ScorchedEarthSettings settings)
        {
            m_Areas.Clear();

            float range = settings.SafeSmolderClusterRange;

            for (int i = 0; i < m_Sources.Count; i++)
            {
                SmolderSource source = m_Sources[i];
                float2 point = source.m_Position.xz;

                int best = -1;
                float bestDistSq = float.MaxValue;

                for (int j = 0; j < m_Areas.Count; j++)
                {
                    // Distance to the rim, so a source just outside a large area still joins it.
                    float distSq = m_Areas[j].m_Circle.DistanceSqTo(point);
                    float reach = m_Areas[j].m_Circle.m_Radius + range;
                    if (distSq > reach * reach)
                    {
                        continue;
                    }

                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = j;
                    }
                }

                if (best >= 0)
                {
                    SmolderArea area = m_Areas[best];
                    area.m_Circle.Encapsulate(point, source.m_Position.y, range * 0.25f);
                    area.m_Strength = math.max(area.m_Strength, source.m_Strength);
                    area.m_Count++;
                    m_Areas[best] = area;
                    continue;
                }

                if (m_Areas.Count >= kMaxAreas)
                {
                    // Out of area slots: fold this source into the nearest existing area so
                    // it still smokes, rather than dropping it silently.
                    int nearest = FindNearest(point);
                    if (nearest >= 0)
                    {
                        SmolderArea area = m_Areas[nearest];
                        area.m_Circle.Encapsulate(point, source.m_Position.y, range * 0.25f);
                        area.m_Strength = math.max(area.m_Strength, source.m_Strength);
                        area.m_Count++;
                        m_Areas[nearest] = area;
                    }

                    continue;
                }

                m_Areas.Add(new SmolderArea
                {
                    m_Circle = new Circle(point, range * 0.4f, source.m_Position.y),
                    m_Strength = source.m_Strength,
                    m_Count = 1,
                });
            }

            MergeOverlappingAreas();

            // Nearest first, so the shared budget goes where the player is looking.
            float3 camera = m_CameraSystem.position;
            m_Areas.Sort((a, b) =>
                math.lengthsq(new float2(a.m_Circle.m_Center.x - camera.x, a.m_Circle.m_Center.y - camera.z))
                    .CompareTo(
                math.lengthsq(new float2(b.m_Circle.m_Center.x - camera.x, b.m_Circle.m_Center.y - camera.z))));
        }

        private int FindNearest(float2 point)
        {
            int best = -1;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < m_Areas.Count; i++)
            {
                float distSq = m_Areas[i].m_Circle.DistanceSqTo(point);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Folds circles that have grown into each other into one. Without this, a fire that
        /// spreads along a street ends up as a row of heavily overlapping areas, each paying
        /// for its own sprites over the same ground.
        /// </summary>
        private void MergeOverlappingAreas()
        {
            for (int i = 0; i < m_Areas.Count; i++)
            {
                for (int j = m_Areas.Count - 1; j > i; j--)
                {
                    if (!Circle.Overlaps(m_Areas[i].m_Circle, m_Areas[j].m_Circle, 0f))
                    {
                        continue;
                    }

                    SmolderArea merged = m_Areas[i];
                    merged.m_Circle = Circle.Union(m_Areas[i].m_Circle, m_Areas[j].m_Circle);
                    merged.m_Strength = math.max(merged.m_Strength, m_Areas[j].m_Strength);
                    merged.m_Count += m_Areas[j].m_Count;

                    m_Areas[i] = merged;
                    m_Areas.RemoveAt(j);
                }
            }
        }

        /// <summary>City-wide smoke budget, capped by what the renderer can draw.</summary>
        private int ComputeBudget(ScorchedEarthSettings settings)
        {
            int budget = (int)(settings.SafeTotalSpriteBudget * kSmokeBudgetShare);

            int renderCap = 0;
            IReadOnlyList<FireEffectCatalogSystem.EffectRef> smokes = m_Catalog.Smokes;
            for (int i = 0; i < smokes.Count; i++)
            {
                renderCap = math.max(renderCap, smokes[i].m_MaxCount);
            }

            return renderCap > 0 ? math.min(budget, renderCap) : budget;
        }

        /// <summary>Fills one circle with scaled smoke sprites. Returns how many it used.</summary>
        private int EmitArea(SmolderArea area, ScorchedEarthSettings settings, int budget, int areaIndex)
        {
            int count = SmokeLayout.SpriteCount(
                area.m_Circle.m_Radius, settings.SafeSmokeSpriteRadius, settings.SafeMaxSmokeSpritesPerArea);

            count = math.min(count, budget);
            if (count <= 0)
            {
                return 0;
            }

            // Solve the sprite radius from the area equation so the circle is actually
            // covered, however few sprites the budget allowed.
            float spriteRadius = SmokeLayout.SpriteRadius(area.m_Circle.m_Radius, count, kOverlap);

            FireEffectCatalogSystem.EffectRef effect;
            float scale;
            if (!SelectSmoke(spriteRadius * 2f, out effect, out scale))
            {
                return 0;
            }

            uint seed = (uint)(areaIndex * 9781 + 1);

            for (int i = 0; i < count; i++)
            {
                float2 flat = SmokeLayout.SpritePosition(in area.m_Circle, i, count, spriteRadius, seed);
                float3 position = new float3(flat.x, area.m_Circle.m_Height + kPlumeHeight, flat.y);

                float intensity = area.m_Strength * SmokeLayout.SpriteIntensity(i, count, kRimFalloff);

                m_Pool.Submit(
                    effect.m_Prefab,
                    position,
                    quaternion.identity,
                    new float3(scale, scale, scale),
                    intensity,
                    Entity.Null);
            }

            return count;
        }

        /// <summary>Picks the smoke asset closest to the required diameter and the residual scale.</summary>
        private bool SelectSmoke(float diameter, out FireEffectCatalogSystem.EffectRef effect, out float scale)
        {
            effect = default(FireEffectCatalogSystem.EffectRef);
            scale = 1f;

            IReadOnlyList<FireEffectCatalogSystem.EffectRef> smokes = m_Catalog.Smokes;
            if (smokes.Count == 0)
            {
                return false;
            }

            float bestError = float.MaxValue;

            for (int i = 0; i < smokes.Count; i++)
            {
                float nominal = NominalSize(smokes[i].m_Rank);
                float required = math.clamp(diameter / nominal, kMinScale, kMaxScale);

                float error = math.abs(math.log(required));
                if (error < bestError)
                {
                    bestError = error;
                    effect = smokes[i];
                    scale = required;
                }
            }

            return effect.m_Prefab != Entity.Null;
        }

        private static float NominalSize(int rank)
        {
            return rank >= 0 && rank < kSmokeNominalSize.Length ? kSmokeNominalSize[rank] : 10f;
        }


        /// <summary>
        /// Starts clean whenever a save is loaded. The sprites from the previous world
        /// were destroyed with it, and the elapsed-frame counter belongs to a different
        /// timeline, so both are dropped rather than carried across.
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Pool.Forget();
            m_Throttle.Reset();
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_Pool != null)
            {
                m_Pool.Dispose(null);
                m_Pool = null;
            }

            base.OnDestroy();
        }

        [Preserve]
        public SmolderAreaSystem()
        {
        }
    }
}
