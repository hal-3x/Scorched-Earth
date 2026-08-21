using Game;
using Game.Buildings;
using Game.Common;
using Game.Events;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Mathematics;
using Damaged = Game.Objects.Damaged;
using ServiceCoverage = Game.Net.ServiceCoverage;
using UnderConstruction = Game.Objects.UnderConstruction;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine.Scripting;
using Tree = Game.Objects.Tree;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Lights a fire on whatever the player clicks.
    ///
    /// <para><b>It starts a real fire.</b> Not a hand-assembled one - it creates an entity from
    /// the fire event prefab's own <see cref="EventData.m_Archetype"/> and points it at the
    /// target, which is exactly what <c>FireHazardSystem</c> does when a fire starts by itself.
    /// The result is indistinguishable from a natural fire: it escalates, spreads, damages,
    /// and calls out fire engines through the game's own machinery. That matters here, because
    /// the last time this mod built event entities by hand the game crashed inside Burst - see
    /// parked-visuals.</para>
    ///
    /// <para><b>Arming, not a toolbar.</b> The mod ships no UI module, so the tool is armed
    /// from a button on its options page. One click arms it, the next click in the world
    /// lights that object, and the tool then hands control back to whatever was active before.
    /// Right-click cancels.</para>
    ///
    /// <para><b>Showing itself.</b> Becoming the active tool costs the game's own hover
    /// highlight, and a tool with no toolbar entry has nothing to show it is armed. Both are
    /// answered by making the object under the cursor glow as though it were already alight,
    /// through the same ember tint the charring pass draws for real fires - so the highlight
    /// and the warning are the same picture.</para>
    /// </summary>
    public sealed partial class IgniteToolSystem : ToolBaseSystem
    {
        public override string toolID => "ScorchedEarth.Ignite";

        private EntityQuery m_FirePrefabQuery;
        private ToolOutputBarrier m_Barrier;
        private DefaultToolSystem m_DefaultTool;
        private ToolBaseSystem m_PreviousTool;

        /// <summary>The object currently glowing under the cursor, if any.</summary>
        private Entity m_Previewing;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            // m_PrefabSystem is inherited from ToolBaseSystem; base.OnCreate has set it.
            m_Barrier = World.GetOrCreateSystemManaged<ToolOutputBarrier>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();

            // Fire event prefabs that are not switched off. Locked is how the game disables an
            // event, so honouring it keeps this tool from lighting a fire the save has banned.
            m_FirePrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<EventData>(),
                ComponentType.ReadOnly<FireData>(),
                ComponentType.Exclude<Locked>());
        }

        /// <summary>Whether the tool has anything to work with in this save.</summary>
        public bool CanIgnite => !m_FirePrefabQuery.IsEmptyIgnoreFilter;

        /// <summary>
        /// Makes this the active tool. The next click in the world lights something.
        /// </summary>
        public void Arm()
        {
            if (!CanIgnite)
            {
                Mod.log.Warn("Cannot arm the ignite tool: this save has no enabled fire event prefab.");
                return;
            }

            if (m_ToolSystem.activeTool == this)
            {
                return;
            }

            m_PreviousTool = m_ToolSystem.activeTool;
            m_ToolSystem.activeTool = this;

            Mod.log.Info("Ignite tool armed - click a glowing object to set it alight.");
        }

        /// <summary>Hands control back to whatever tool was active when this one was armed.</summary>
        private void Disarm()
        {
            ToolBaseSystem previous = m_PreviousTool;
            m_PreviousTool = null;

            m_ToolSystem.activeTool = previous ?? (ToolBaseSystem)m_DefaultTool;
        }

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();

            // Everything that can burn: buildings and the trees around them.
            m_ToolRaycastSystem.typeMask = TypeMask.StaticObjects;
            m_ToolRaycastSystem.collisionMask = CollisionMask.OnGround | CollisionMask.Overground;
        }

        [Preserve]
        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;
        }

        [Preserve]
        protected override void OnStopRunning()
        {
            // Whatever was glowing must stop, however the tool was left - cancelled, switched
            // away from, or used.
            ClearPreview();

            applyAction.shouldBeEnabled = false;
            cancelAction.shouldBeEnabled = false;
            base.OnStopRunning();
        }

        /// <summary>
        /// ToolBaseSystem seals the plain OnUpdate and hands tools this one instead, so the
        /// tool phase can thread a job dependency through. Nothing here schedules work - one
        /// click creates one event - so the handle passes straight back.
        /// </summary>
        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            if (cancelAction.WasPressedThisFrame())
            {
                Mod.log.Info("Ignite tool cancelled.");
                Disarm();
                return inputDeps;
            }

            Entity target;
            RaycastHit hit;
            if (!GetRaycastResult(out target, out hit))
            {
                target = Entity.Null;
            }

            // Every frame, so the glow follows the cursor.
            Preview(target);

            if (!applyAction.WasPressedThisFrame() || target == Entity.Null)
            {
                return inputDeps;
            }

            if (Ignite(target))
            {
                // The object is about to be genuinely alight, so the preview has to come off
                // first - otherwise it would be left holding a glow the fire does not own.
                ClearPreview();

                // One click, one fire. Staying armed would turn a stray click into an
                // accidental second fire, and fires here are not cheap to undo.
                Disarm();
            }

            return inputDeps;
        }

        /// <summary>Creates a fire event aimed at one object, the same way the game does.</summary>
        private bool Ignite(Entity target)
        {
            EventTargetType wanted = EntityManager.HasComponent<Tree>(target)
                ? EventTargetType.WildTree
                : EventTargetType.Building;

            Entity eventPrefab = FindFirePrefab(wanted);
            if (eventPrefab == Entity.Null)
            {
                Mod.log.Warn($"No fire event prefab targets {wanted}; nothing was ignited.");
                return false;
            }

            EventData eventData = EntityManager.GetComponentData<EventData>(eventPrefab);

            EntityCommandBuffer commands = m_Barrier.CreateCommandBuffer();

            Entity fire = commands.CreateEntity(eventData.m_Archetype);
            commands.SetComponent(fire, new PrefabRef(eventPrefab));
            commands.SetBuffer<TargetElement>(fire).Add(new TargetElement(target));

            Mod.log.Info($"Ignite tool set fire to entity {target.Index} using "
                       + $"{m_PrefabSystem.GetPrefabName(eventPrefab)} ({wanted}).");
            return true;
        }

        /// <summary>
        /// The fire event prefab meant for this kind of target.
        ///
        /// <para>Carrying <see cref="FireData"/> is not enough to identify a fire. A lightning
        /// strike carries it too, because lightning sets things alight - so taking the first
        /// match in the query struck the sky instead of lighting the building.
        /// <c>FireHazardSystem</c> picks by <see cref="FireData.m_RandomTargetType"/>, and so
        /// does this.</para>
        /// </summary>
        private Entity FindFirePrefab(EventTargetType wanted)
        {
            Entity found = Entity.Null;

            NativeArray<Entity> prefabs = m_FirePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    FireData fireData = EntityManager.GetComponentData<FireData>(prefab);

                    Mod.Verbose(() => "Fire event candidate: "
                                    + m_PrefabSystem.GetPrefabName(prefab)
                                    + " -> " + fireData.m_RandomTargetType);

                    if (fireData.m_RandomTargetType == wanted && found == Entity.Null)
                    {
                        found = prefab;
                    }
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            return found;
        }

        /// <summary>
        /// Makes the object under the cursor glow as though it were already alight, and stops
        /// the previous one glowing.
        /// </summary>
        private void Preview(Entity target)
        {
            if (target == m_Previewing)
            {
                return;
            }

            ClearPreview();

            if (target == Entity.Null || !EntityManager.Exists(target))
            {
                return;
            }

            // Whether the glow will actually show depends on the object having mesh colours
            // for the charring pass to rewrite. Buildings do; whether a living tree does is
            // exactly the question when a tree refuses to light up under the cursor, so the
            // answer is logged rather than guessed at.
            if (Mod.IsVerbose)
            {
                bool hasMesh = EntityManager.HasBuffer<MeshColor>(target);
                int meshLength = hasMesh
                    ? EntityManager.GetBuffer<MeshColor>(target).Length
                    : -1;

                string prefabName = "<none>";
                bool destructible = false;
                bool spawnable = false;
                bool buildingData = false;

                if (EntityManager.HasComponent<PrefabRef>(target))
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
                    if (prefab != Entity.Null)
                    {
                        prefabName = m_PrefabSystem.GetPrefabName(prefab);
                        destructible = EntityManager.HasComponent<DestructibleObjectData>(prefab);
                        spawnable = EntityManager.HasComponent<SpawnableBuildingData>(prefab);
                        buildingData = EntityManager.HasComponent<BuildingData>(prefab);
                    }
                }

                Mod.log.Info("Preview target " + target.Index + " (" + prefabName + ")"
                           + ": tree=" + EntityManager.HasComponent<Tree>(target)
                           + " plant=" + EntityManager.HasComponent<Game.Objects.Plant>(target)
                           + " building=" + EntityManager.HasComponent<Game.Buildings.Building>(target)
                           + " meshColor=" + hasMesh + " (" + meshLength + " entries)"
                           + " charred=" + EntityManager.HasComponent<Charred>(target));

                // The two that decide whether this mod can touch its flammability at all:
                // destructible means it has its own hazard to scale, spawnable means the
                // zone multiplier reaches it. Neither means it is out of reach.
                Mod.log.Info("  prefab: destructibleObjectData=" + destructible
                           + " spawnableBuildingData=" + spawnable
                           + " buildingData=" + buildingData
                           + (destructible || spawnable
                                ? ""
                                : "  <-- NOT REACHED by any current lever"));

                DumpHazard(target);
            }

            // The charring pass already knows how to draw a glow; it only needs somewhere to
            // keep the level and a colour cache to compute it from.
            if (!EntityManager.HasComponent<Charred>(target))
            {
                EntityManager.AddComponentData(target, default(Charred));

                if (EntityManager.HasBuffer<MeshColor>(target)
                    && !EntityManager.HasBuffer<OriginalMeshColor>(target))
                {
                    EntityManager.AddBuffer<OriginalMeshColor>(target);
                }
            }

            Charred charred = EntityManager.GetComponentData<Charred>(target);
            charred.m_Ember = 1f;
            EntityManager.SetComponentData(target, charred);

            EntityManager.AddComponent<IgnitePreview>(target);
            EntityManager.AddComponent<BatchesUpdated>(target);

            m_Previewing = target;
        }

        /// <summary>
        /// Replays the game's own fire-hazard chain for one building and prints every step.
        ///
        /// <para>Three guesses in a row about why some buildings will not catch have been
        /// wrong, each of them reasoning from which components a prefab carries rather than
        /// from the number the simulation actually rolls against. This computes that number
        /// the same way <c>EventHelpers.GetFireHazard</c> does, term by term, so two buildings
        /// on one street can be compared directly instead of argued about.</para>
        /// </summary>
        private void DumpHazard(Entity target)
        {
            if (!EntityManager.HasComponent<Building>(target)
                || !EntityManager.HasComponent<PrefabRef>(target))
            {
                return;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(target).m_Prefab;
            if (prefab == Entity.Null)
            {
                return;
            }

            // Step 1 - the base, which is the prefab's own hazard or a hardcoded 100.
            float baseHazard = 100f;
            bool ownHazard = EntityManager.HasComponent<DestructibleObjectData>(prefab);
            if (ownHazard)
            {
                baseHazard = EntityManager.GetComponentData<DestructibleObjectData>(prefab).m_FireHazard;
            }

            float hazard = baseHazard;

            // Step 2 - under construction zeroes it outright.
            bool underConstruction = false;
            if (EntityManager.HasComponent<UnderConstruction>(target))
            {
                UnderConstruction uc = EntityManager.GetComponentData<UnderConstruction>(target);
                underConstruction = uc.m_NewPrefab == Entity.Null && uc.m_Progress < byte.MaxValue;
            }

            // Step 3 - building level, then the zone's multiplier.
            int level = 0;
            float zoneMultiplier = 1f;
            string zoneName = "<none>";
            if (EntityManager.HasComponent<SpawnableBuildingData>(prefab))
            {
                SpawnableBuildingData spawnable = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);
                level = spawnable.m_Level;
                hazard *= 1f - (level - 1) * 0.03f;

                if (spawnable.m_ZonePrefab != Entity.Null
                    && EntityManager.HasComponent<ZonePropertiesData>(spawnable.m_ZonePrefab))
                {
                    zoneMultiplier = EntityManager.GetComponentData<ZonePropertiesData>(spawnable.m_ZonePrefab)
                                                  .m_FireHazardMultiplier;
                    zoneName = m_PrefabSystem.GetPrefabName(spawnable.m_ZonePrefab);
                    hazard *= zoneMultiplier;
                }
            }

            // Step 4 - fire service coverage, which can divide by a hundred on its own.
            float coverage = 0f;
            Building building = EntityManager.GetComponentData<Building>(target);
            if (building.m_RoadEdge != Entity.Null
                && EntityManager.HasBuffer<ServiceCoverage>(building.m_RoadEdge))
            {
                coverage = NetUtils.GetServiceCoverage(
                    EntityManager.GetBuffer<ServiceCoverage>(building.m_RoadEdge),
                    CoverageService.FireRescue,
                    building.m_CurvePosition);
                hazard *= math.max(0.01f, 1f - coverage * 0.01f);
            }

            // Step 5 - existing damage sharply reduces it: the factor is raised to the fourth.
            float damageFactor = 1f;
            if (EntityManager.HasComponent<Damaged>(target))
            {
                Damaged damaged = EntityManager.GetComponentData<Damaged>(target);
                float remaining = math.max(0f, 1f - math.csum(damaged.m_Damage.yz));
                damageFactor = remaining * remaining * remaining * remaining;
                hazard *= damageFactor;
            }

            // And the footprint, which decides how far away the spread check thinks it is.
            float narrowHalf = -1f;
            if (EntityManager.HasComponent<ObjectGeometryData>(prefab))
            {
                narrowHalf = math.cmin(EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size.xz) * 0.5f;
            }

            Mod.log.Info("  hazard chain: base=" + baseHazard + (ownHazard ? " (own)" : " (default 100)")
                       + " level=" + level
                       + " zone=" + zoneName + " x" + zoneMultiplier
                       + " coverage=" + coverage
                       + " damageFactor=" + damageFactor
                       + " underConstruction=" + underConstruction
                       + " -> FINAL HAZARD " + (underConstruction ? 0f : hazard)
                       + " | narrowHalfWidth=" + narrowHalf);
        }

        /// <summary>Stops the previewed object glowing and leaves it as it was found.</summary>
        private void ClearPreview()
        {
            Entity previous = m_Previewing;
            m_Previewing = Entity.Null;

            if (previous == Entity.Null || !EntityManager.Exists(previous))
            {
                return;
            }

            if (EntityManager.HasComponent<IgnitePreview>(previous))
            {
                EntityManager.RemoveComponent<IgnitePreview>(previous);
            }

            if (EntityManager.HasComponent<Charred>(previous))
            {
                Charred charred = EntityManager.GetComponentData<Charred>(previous);
                charred.m_Ember = 0f;
                EntityManager.SetComponentData(previous, charred);

                // A preview on something that was never charred leaves a component holding
                // nothing. RecoverySystem retires those on its next tick, since their char
                // level is zero - and it skips anything still burning, so a real fire keeps
                // the soot it earned.
            }

            EntityManager.AddComponent<BatchesUpdated>(previous);
        }

        // The tool places no prefab of its own; it aims an event at something already there.
        public override PrefabBase GetPrefab()
        {
            return null;
        }

        public override bool TrySetPrefab(PrefabBase prefab)
        {
            return false;
        }

        [Preserve]
        public IgniteToolSystem()
        {
        }
    }
}
