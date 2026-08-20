using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Events;
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
    /// Paints burnt ground under fires, so land that has been through one goes on looking
    /// like it.
    ///
    /// <para><b>How.</b> The game's surface painter is not an area or a mesh - it is a
    /// compute-shader stroke into a terrain splatmap, reached through
    /// <c>TerrainMaterialSystem.ApplyBrush</c>. That is the whole reason this mod can do
    /// ground scorching at all: it creates no entities, builds no geometry, and the splatmap
    /// is serialized with the save, so the scarring persists without this mod having to
    /// remember anything.</para>
    ///
    /// <para><b>The channel problem.</b> Four channels are paintable, <c>Extra1</c> through
    /// <c>Extra4</c>. They are a shared, map-wide resource with no allocation mechanism, and
    /// what each one looks like is decided by the map's terrain render settings rather than
    /// by this mod - one map's Extra3 is its dirt, another's is something else entirely. So
    /// the channel is a user setting rather than a constant.</para>
    ///
    /// <para><b>Why it never erases.</b> Erasing is <c>TerrainMaterialType.None</c>, which
    /// runs the erase kernel with every channel selected - it would wipe the player's own
    /// painting wherever a fire happened to reach. Burn scars are therefore permanent, and
    /// the player paints over them if they want them gone. Recovery in this mod fades soot
    /// off objects; it deliberately does not un-scorch the ground.</para>
    /// </summary>
    public sealed partial class ScorchSurfaceSystem : GameSystemBase
    {
        /// <summary>Tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 64;

        /// <summary>
        /// Strokes allowed per tick. Each one is a compute dispatch plus a command-buffer
        /// execute on the main thread - cheap enough for a brush the player is dragging,
        /// far too expensive to run once per burning tree in a forest fire.
        /// </summary>
        private const int kStrokeBudget = 24;

        private EntityQuery m_BurningQuery;
        private EntityQuery m_BurnedTreeQuery;
        private EntityQuery m_TerraformingQuery;
        private EntityQuery m_BrushQuery;

        private TerrainMaterialSystem m_TerrainMaterialSystem;
        private PrefabSystem m_PrefabSystem;
        private SimulationSystem m_SimulationSystem;
        private UpdateThrottle m_Throttle;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<Transform> m_TransformType;

        /// <summary>
        /// Ground cells already scorched, so a fire that burns for a while does not restroke
        /// the same patch every tick. Not serialized: the splatmap itself carries the result,
        /// and this only has to stop repeat work inside one session.
        /// </summary>
        private NativeParallelHashSet<int2> m_Painted;

        private Entity m_ToolPrefab;
        private Entity m_BrushPrefab;
        private TerrainMaterialType m_ResolvedType;
        private bool m_ResolveFailed;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_TerrainMaterialSystem = World.GetOrCreateSystemManaged<TerrainMaterialSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_BurningQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // The aftermath, not just the flames. A tree burns out in seconds and this system
            // ticks every 64 frames, so keying only on OnFire misses most of a forest fire -
            // but the trees it killed stand there for weeks, and they mark exactly the ground
            // that ought to look burnt.
            m_BurnedTreeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<FireKilledTree>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // The painter's two ingredients, both plain prefabs the game already ships:
            // a terraforming prefab that names the splatmap channel, and a brush prefab that
            // supplies the stroke's shape texture.
            m_TerraformingQuery = GetEntityQuery(ComponentType.ReadOnly<TerraformingData>());
            m_BrushQuery = GetEntityQuery(ComponentType.ReadOnly<BrushData>());

            m_EntityType = GetEntityTypeHandle();
            m_TransformType = GetComponentTypeHandle<Transform>(true);

            m_Painted = new NativeParallelHashSet<int2>(1024, Allocator.Persistent);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_Painted.IsCreated)
            {
                m_Painted.Dispose();
            }

            base.OnDestroy();
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kBaseInterval;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;
            if (settings == null || !settings.ScorchGround)
            {
                return;
            }

            uint elapsed;
            if (!m_Throttle.ShouldRun(m_SimulationSystem.frameIndex, kBaseInterval, out elapsed))
            {
                return;
            }

            if (m_BurningQuery.IsEmptyIgnoreFilter && m_BurnedTreeQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (!ResolvePrefabs(settings.ScorchMaterialType))
            {
                return;
            }

            float radius = settings.ScorchRadius;
            float opacity = settings.ScorchOpacityNormalized;

            // Cells are a little smaller than the brush so neighbouring strokes overlap
            // rather than leaving a grid of untouched seams between them.
            float cellSize = math.max(1f, radius * 0.75f);

            m_EntityType.Update(this);
            m_TransformType.Update(this);

            int budget = kStrokeBudget;

            budget = ScorchUnder(m_BurningQuery, cellSize, radius, opacity, budget);
            ScorchUnder(m_BurnedTreeQuery, cellSize, radius, opacity, budget);
        }

        /// <summary>
        /// Lays scorch under everything in a query that stands on ground no stroke has covered
        /// yet. Returns what is left of the budget.
        /// </summary>
        private int ScorchUnder(EntityQuery query, float cellSize, float radius, float opacity, int budget)
        {
            if (budget <= 0)
            {
                return 0;
            }

            NativeArray<ArchetypeChunk> chunks = query.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length && budget > 0; c++)
                {
                    NativeArray<Transform> transforms = chunks[c].GetNativeArray(ref m_TransformType);

                    for (int i = 0; i < transforms.Length && budget > 0; i++)
                    {
                        float3 position = transforms[i].m_Position;

                        int2 cell = (int2)math.floor(position.xz / cellSize);
                        if (!m_Painted.Add(cell))
                        {
                            continue;
                        }

                        Paint(position, radius, opacity);
                        budget--;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return budget;
        }

        /// <summary>Lays one stroke of burnt ground down at a world position.</summary>
        private void Paint(float3 position, float radius, float opacity)
        {
            Brush brush = default(Brush);
            brush.m_Tool = m_ToolPrefab;
            brush.m_Position = position;

            // Only position, size and angle reach the splatmap - ToolUtils.GetBounds ignores
            // the rest - but they are filled in so the struct never carries stale zeroes into
            // anything the game adds later.
            brush.m_Target = position;
            brush.m_Start = position;
            brush.m_Angle = 0f;
            brush.m_Size = radius;
            brush.m_Strength = opacity;
            brush.m_Opacity = opacity;

            m_TerrainMaterialSystem.ApplyBrush(brush, m_BrushPrefab);
        }

        /// <summary>
        /// Finds the two prefabs the painter needs, once. The channel is a setting, so the
        /// search reruns whenever the player changes it.
        /// </summary>
        private bool ResolvePrefabs(TerrainMaterialType wanted)
        {
            if (m_ToolPrefab != Entity.Null && m_BrushPrefab != Entity.Null && m_ResolvedType == wanted)
            {
                return true;
            }

            if (m_ResolveFailed && m_ResolvedType == wanted)
            {
                return false;   // Already looked and came up empty; do not rescan every tick.
            }

            m_ResolvedType = wanted;
            m_ToolPrefab = FindToolPrefab(wanted);
            m_BrushPrefab = FindBrushPrefab();
            m_ResolveFailed = m_ToolPrefab == Entity.Null || m_BrushPrefab == Entity.Null;

            if (m_ResolveFailed)
            {
                Mod.log.Warn($"Ground scorching is on, but the painter prefabs could not be found "
                           + $"(channel {wanted}: tool={m_ToolPrefab != Entity.Null}, "
                           + $"brush={m_BrushPrefab != Entity.Null}). Turn on verbose logging to "
                           + $"list what this save actually has.");
                return false;
            }

            Mod.log.Info($"Ground scorching using channel {wanted} "
                       + $"({m_PrefabSystem.GetPrefabName(m_ToolPrefab)}) "
                       + $"with brush {m_PrefabSystem.GetPrefabName(m_BrushPrefab)}.");
            return true;
        }

        /// <summary>The terraforming prefab that paints the requested splatmap channel.</summary>
        private Entity FindToolPrefab(TerrainMaterialType wanted)
        {
            Entity found = Entity.Null;

            NativeArray<Entity> candidates = m_TerraformingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    TerraformingPrefab prefab;
                    if (!m_PrefabSystem.TryGetPrefab(candidates[i], out prefab))
                    {
                        continue;
                    }

                    // Only the surface-painting terraforming prefabs carry this; the
                    // level/slope/shift ones do not, which is exactly how they are told apart.
                    TerrainMaterialProperties properties;
                    if (!prefab.TryGet(out properties))
                    {
                        continue;
                    }

                    Mod.Verbose(() => $"Surface painter candidate: {prefab.name} -> {properties.m_Type}");

                    if (properties.m_Type == wanted && found == Entity.Null)
                    {
                        found = candidates[i];
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            return found;
        }

        /// <summary>
        /// The plainest brush the game has loaded.
        ///
        /// <para>Priority orders the brush list the painter UI shows, so the lowest is the
        /// default round one rather than a shaped mountain or ridge brush - those carry
        /// falloff meant for sculpting terrain, which lands as a small patterned smudge
        /// instead of an even scar. Chosen by that ordering rather than by asset name, which
        /// a patch could rename; every candidate is logged so the pick can be checked.</para>
        /// </summary>
        private Entity FindBrushPrefab()
        {
            Entity found = Entity.Null;
            int bestPriority = int.MaxValue;

            NativeArray<Entity> candidates = m_BrushQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < candidates.Length; i++)
                {
                    BrushPrefab prefab;
                    if (!m_PrefabSystem.TryGetPrefab(candidates[i], out prefab))
                    {
                        continue;
                    }

                    if (prefab.m_Texture == null)
                    {
                        continue;   // ApplyBrush would hand a null texture to the compute shader.
                    }

                    Mod.log.Info($"Brush candidate: {prefab.name} (priority {prefab.m_Priority})");

                    if (prefab.m_Priority < bestPriority)
                    {
                        bestPriority = prefab.m_Priority;
                        found = candidates[i];
                    }
                }
            }
            finally
            {
                candidates.Dispose();
            }

            return found;
        }

        /// <summary>
        /// A different save has a different splatmap, so what this session has already painted
        /// says nothing about it.
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            m_Throttle.Reset();

            if (m_Painted.IsCreated)
            {
                m_Painted.Clear();
            }
        }

        [Preserve]
        public ScorchSurfaceSystem()
        {
        }
    }
}
