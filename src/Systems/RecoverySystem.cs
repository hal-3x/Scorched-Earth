using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Events;
using Game.Objects;
using Game.Rendering;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Tree = Game.Objects.Tree;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Undoes fire damage over time: soot fades off buildings, and trees killed by fire
    /// come back to life.
    ///
    /// <para>Recovery only runs on objects that are no longer burning, so a building that
    /// reignites keeps blackening instead of cleaning itself mid-fire.</para>
    ///
    /// <para>The end state is exact rather than approximate. Charring is removed by
    /// switching the per-instance colour override off, which hands colour back to the
    /// game's own <c>MeshColorSystem</c>; the mod never has to reconstruct what the
    /// original colours were.</para>
    /// </summary>
    public sealed partial class RecoverySystem : GameSystemBase
    {
        /// <summary>Below this char level the object is treated as clean.</summary>
        private const float kCleanThreshold = 0.01f;

        /// <summary>Colour change smaller than this is not worth dirtying a render batch for.</summary>
        private const float kRepaintThreshold = 0.02f;

        /// <summary>Tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 256;

        private EntityQuery m_CharredQuery;
        private EntityQuery m_DeadTreeQuery;
        private EndFrameBarrier m_Barrier;
        private SimulationSystem m_SimulationSystem;
        private UpdateThrottle m_Throttle;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            // Charred, not currently burning, and still standing.
            m_CharredQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { ComponentType.ReadWrite<Charred>() },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Destroyed>(),
                },
            });

            m_DeadTreeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadWrite<FireKilledTree>(),
                    ComponentType.ReadWrite<Tree>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });
        }

        /// <summary>
        /// Recovery is measured in in-game days, so it ticks far more slowly than the fire
        /// visuals without any visible stepping. Progress is computed from frames actually
        /// elapsed, so the rate is unaffected by this choice.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kBaseInterval;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;
            if (settings == null)
            {
                return;
            }

            uint elapsed;
            if (!m_Throttle.ShouldRun(m_SimulationSystem.frameIndex, kBaseInterval, out elapsed))
            {
                return;
            }

            EntityCommandBuffer commands = m_Barrier.CreateCommandBuffer();

            FadeCharring(settings, elapsed, ref commands);
            RegrowTrees(settings, elapsed, ref commands);
        }

        /// <summary>Steps every charred object toward clean and repaints when it has moved enough.</summary>
        private void FadeCharring(ScorchedEarthSettings settings, float elapsed, ref EntityCommandBuffer commands)
        {
            float step = settings.CharRecoveryPerFrame * elapsed;
            if (step <= 0f)
            {
                return;
            }

            NativeArray<Entity> entities = m_CharredQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Charred charred = EntityManager.GetComponentData<Charred>(entity);

                    charred.m_Amount = math.max(0f, charred.m_Amount - step);

                    if (charred.m_Amount <= kCleanThreshold)
                    {
                        Restore(entity, charred, ref commands);
                        continue;
                    }

                    if (math.abs(charred.m_Amount - charred.m_AppliedAmount) < kRepaintThreshold)
                    {
                        // Not visibly different yet - keep the progress, skip the repaint.
                        commands.SetComponent(entity, charred);
                        continue;
                    }

                    CharringSystem.RequestRepaint(entity, ref charred, ref commands);
                    commands.SetComponent(entity, charred);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Hands colour control back and drops the mod's bookkeeping.
        ///
        /// Dropping the mod's components is enough: MeshColorSystem recomputes the prefab's
        /// own colours on the next batch update, so the result is exactly the original rather
        /// than an approximation of it, and any override another mod owns is left alone.
        /// </summary>
        private void Restore(Entity entity, Charred charred, ref EntityCommandBuffer commands)
        {
            commands.RemoveComponent<Charred>(entity);
            commands.RemoveComponent<OriginalMeshColor>(entity);

            // Dirtying the batch makes MeshColorSystem rebuild the object's colours from its
            // prefab on the next frame. Nothing has to remember what they were.
            commands.AddComponent<BatchesUpdated>(entity);
        }

        /// <summary>
        /// Grows fire-killed trees back. Progress is tracked separately from charring so a
        /// tree can finish cleaning off its soot long before it is green again - which is
        /// what a burned stand of trees actually looks like a season later.
        /// </summary>
        private void RegrowTrees(ScorchedEarthSettings settings, float elapsed, ref EntityCommandBuffer commands)
        {
            float step = settings.TreeRecoveryPerFrame * elapsed;
            if (step <= 0f)
            {
                return;
            }

            NativeArray<Entity> entities = m_DeadTreeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    FireKilledTree killed = EntityManager.GetComponentData<FireKilledTree>(entity);

                    killed.m_Regrowth = math.min(1f, killed.m_Regrowth + step);

                    if (killed.m_Regrowth < 1f)
                    {
                        commands.SetComponent(entity, killed);
                        continue;
                    }

                    Tree tree = EntityManager.GetComponentData<Tree>(entity);

                    // Come back as a sapling rather than snapping straight to the old size;
                    // the vanilla growth simulation takes it the rest of the way.
                    tree.m_State = TreeState.Teen;
                    tree.m_Growth = (byte)math.min((int)killed.m_OriginalGrowth, 64);

                    commands.SetComponent(entity, tree);
                    commands.RemoveComponent<FireKilledTree>(entity);
                    commands.AddComponent<BatchesUpdated>(entity);
                    commands.AddComponent<Updated>(entity);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }


        /// <summary>
        /// Drops the elapsed-frame counter when a save is loaded; the new world's frame
        /// index belongs to a different timeline.
        /// </summary>
        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Throttle.Reset();
        }

        [Preserve]
        public RecoverySystem()
        {
        }
    }
}
