using System.Collections.Generic;
using Colossal.Serialization.Entities;
using ScorchedEarth.Geometry;
using Game;
using Game.Common;
using Game.Events;
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
    /// Places flame sprites along a line across each burning object, instead of the
    /// vanilla arrangement of a fixed handful of flames at fixed offsets.
    ///
    /// <para>The line comes from the burning object's own footprint: it runs along the
    /// longest horizontal axis of the prefab's geometry, and its width is the shorter
    /// axis. That is what makes a wide warehouse burn as a wide front and a lamppost burn
    /// as a point, without any per-asset authoring.</para>
    ///
    /// <para>Cost is bounded twice over. Per fire, the sprite count is clamped by
    /// <see cref="ScorchedEarthSettings.MaxFlameSpritesPerFire"/>, and the remaining sprites
    /// are <em>stretched</em> to close the gaps so the front still reads as a continuous
    /// line rather than a dotted one. City-wide, a shared budget is spent nearest-camera
    /// first, so a distant firestorm cannot starve the fire the player is looking at.</para>
    /// </summary>
    public sealed partial class FireLineSystem : GameSystemBase
    {
        /// <summary>
        /// Roughly how many metres one flame effect covers at scale 1, indexed by the
        /// catalog's flame rank (tiny, small, medium, moving-medium, big).
        ///
        /// These are visual sizes, not authored data - the game does not publish a size for
        /// a VFX asset. Picking the closest-sized asset first and only then scaling keeps
        /// the scale multiplier near 1, which matters because a heavily scaled particle
        /// effect stops looking like fire.
        /// </summary>
        private static readonly float[] kFlameNominalSize = { 1.5f, 3f, 6f, 6f, 12f };

        /// <summary>Scale multipliers outside this range distort the effect too much.</summary>
        private const float kMinScale = 0.35f;
        private const float kMaxScale = 3.0f;

        /// <summary>Share of the city-wide sprite budget reserved for flames.</summary>
        private const float kFlameBudgetShare = 0.65f;

        /// <summary>Fire intensity (0..100) at which a fire is drawn at full strength.</summary>
        private const float kFullIntensity = 40f;

        private struct BurningObject
        {
            public Entity m_Entity;
            public float3 m_Position;
            public quaternion m_Rotation;
            public float3 m_Size;
            public float m_Intensity;
            public float m_CameraDistanceSq;
        }

        /// <summary>Fastest tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 4;

        private EntityQuery m_BurningQuery;
        private SimulationSystem m_SimulationSystem;
        private UpdateThrottle m_Throttle;
        private FireEffectCatalogSystem m_Catalog;
        private CameraUpdateSystem m_CameraSystem;
        private EffectSpritePool m_Pool;
        private EndFrameBarrier m_Barrier;

        private readonly List<BurningObject> m_Fires = new List<BurningObject>();

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Catalog = World.GetOrCreateSystemManaged<FireEffectCatalogSystem>();
            m_CameraSystem = World.GetOrCreateSystemManaged<CameraUpdateSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_Pool = new EffectSpritePool(EntityManager, EffectRole.Flame);

            m_BurningQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                    ComponentType.ReadOnly<Transform>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        /// <summary>Fastest tick rate; the user's interval is applied on top in OnUpdate.</summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kBaseInterval;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;

            if (settings == null || !settings.FireLines || !m_Catalog.Ready)
            {
                // Release anything left over from when the feature was last on.
                if (m_Pool.PooledCount > 0)
                {
                    m_Pool.Begin(m_Barrier.CreateCommandBuffer());
                    m_Pool.End();
                }

                return;
            }

            uint elapsed;
            if (!m_Throttle.ShouldRun(m_SimulationSystem.frameIndex, settings.SafeUpdateInterval, out elapsed))
            {
                return;
            }

            CollectFires();

            m_Pool.Begin(m_Barrier.CreateCommandBuffer());

            int budget = ComputeBudget(settings);
            for (int i = 0; i < m_Fires.Count && budget > 0; i++)
            {
                budget -= EmitFire(m_Fires[i], settings, budget);
            }

            m_Pool.End();

            Mod.Verbose(() => "Fire fronts: " + m_Fires.Count + " fire(s), " + m_Pool.ActiveCount + " flame sprite(s).");
        }

        /// <summary>Gathers burning objects with the geometry needed to lay out a front.</summary>
        private void CollectFires()
        {
            m_Fires.Clear();

            float3 camera = m_CameraSystem.position;

            NativeArray<Entity> entities = m_BurningQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];

                    OnFire onFire = EntityManager.GetComponentData<OnFire>(entity);
                    if (onFire.m_Intensity <= 0f)
                    {
                        continue;
                    }

                    Transform transform = EntityManager.GetComponentData<Transform>(entity);
                    PrefabRef prefabRef = EntityManager.GetComponentData<PrefabRef>(entity);

                    // Objects without geometry (abstract events, markers) have no footprint
                    // to spread a front along, so they are left to burn as a single point.
                    float3 size = new float3(2f, 2f, 2f);
                    if (EntityManager.HasComponent<ObjectGeometryData>(prefabRef.m_Prefab))
                    {
                        ObjectGeometryData geometry =
                            EntityManager.GetComponentData<ObjectGeometryData>(prefabRef.m_Prefab);
                        size = math.max(geometry.m_Size, new float3(0.5f, 0.5f, 0.5f));
                    }

                    m_Fires.Add(new BurningObject
                    {
                        m_Entity = entity,
                        m_Position = transform.m_Position,
                        m_Rotation = transform.m_Rotation,
                        m_Size = size,
                        m_Intensity = onFire.m_Intensity,
                        m_CameraDistanceSq = math.lengthsq(transform.m_Position - camera),
                    });
                }
            }
            finally
            {
                entities.Dispose();
            }

            // Nearest first: the shared budget is spent where the player can see it.
            m_Fires.Sort(CompareByCameraDistance);
        }

        private static int CompareByCameraDistance(BurningObject a, BurningObject b)
        {
            return a.m_CameraDistanceSq.CompareTo(b.m_CameraDistanceSq);
        }

        /// <summary>
        /// City-wide flame budget: the user's share of the total, additionally capped by what
        /// the renderer can actually draw for the chosen effect.
        /// </summary>
        private int ComputeBudget(ScorchedEarthSettings settings)
        {
            int budget = (int)(settings.SafeTotalSpriteBudget * kFlameBudgetShare);

            int renderCap = 0;
            IReadOnlyList<FireEffectCatalogSystem.EffectRef> flames = m_Catalog.Flames;
            for (int i = 0; i < flames.Count; i++)
            {
                renderCap = math.max(renderCap, flames[i].m_MaxCount);
            }

            return renderCap > 0 ? math.min(budget, renderCap) : budget;
        }

        /// <summary>Lays one fire out as a line of sprites. Returns how many sprites it used.</summary>
        private int EmitFire(BurningObject fire, ScorchedEarthSettings settings, int budget)
        {
            FireLine line = FireLine.FromFootprint(
                fire.m_Position, fire.m_Rotation, fire.m_Size, settings.FireLineCoverageNormalized);

            int count = line.SpriteCount(settings.SafeFireSpriteSpacing, settings.SafeMaxFlameSpritesPerFire);
            count = math.min(count, budget);
            if (count <= 0)
            {
                return 0;
            }

            // Span is the gap the sprites have to bridge. When the budget clamped the count,
            // this grows and the sprites are scaled up to match, so the front stays solid.
            float span = math.max(line.SpriteSpan(count), line.m_Width);

            FireEffectCatalogSystem.EffectRef effect;
            float scale;
            if (!SelectFlame(span, out effect, out scale))
            {
                return 0;
            }

            float strength = math.saturate(fire.m_Intensity / kFullIntensity);

            // A weak fire is a low flicker, not a small copy of a big fire, so height is
            // scaled harder by intensity than the footprint is.
            float3 spriteScale = new float3(scale, scale * math.lerp(0.45f, 1f, strength), scale);

            for (int i = 0; i < count; i++)
            {
                float3 position = line.SpritePosition(i, count);
                position += line.LateralOffset(i, line.m_Width * 0.25f);

                m_Pool.Submit(
                    effect.m_Prefab,
                    position,
                    fire.m_Rotation,
                    spriteScale,
                    strength,
                    fire.m_Entity);
            }

            return count;
        }

        /// <summary>
        /// Picks the flame asset whose natural size is closest to the span each sprite has
        /// to cover, then returns the residual scale needed to match it exactly.
        /// </summary>
        private bool SelectFlame(float span, out FireEffectCatalogSystem.EffectRef effect, out float scale)
        {
            effect = default(FireEffectCatalogSystem.EffectRef);
            scale = 1f;

            IReadOnlyList<FireEffectCatalogSystem.EffectRef> flames = m_Catalog.Flames;
            if (flames.Count == 0)
            {
                return false;
            }

            float bestError = float.MaxValue;

            for (int i = 0; i < flames.Count; i++)
            {
                float nominal = NominalSize(flames[i].m_Rank);
                float required = math.clamp(span / nominal, kMinScale, kMaxScale);

                // Prefer the asset that needs the least scaling away from 1.
                float error = math.abs(math.log(required));
                if (error < bestError)
                {
                    bestError = error;
                    effect = flames[i];
                    scale = required;
                }
            }

            return effect.m_Prefab != Entity.Null;
        }

        private static float NominalSize(int rank)
        {
            return rank >= 0 && rank < kFlameNominalSize.Length ? kFlameNominalSize[rank] : 4f;
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
        public FireLineSystem()
        {
        }
    }
}
