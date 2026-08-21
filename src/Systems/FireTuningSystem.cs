using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Retunes the game's fire simulation by rewriting the prefab data it reads.
    ///
    /// <para><b>Why prefab data.</b> Fire spread is not hardcoded - <c>FireSimulationSystem</c>
    /// reads <see cref="FireData"/> off the fire event prefab and the target's flammability off
    /// its own prefab, every time. Writing different numbers into those is enough to change how
    /// fire behaves, with no code patching, no Harmony, and no new systems in the
    /// simulation.</para>
    ///
    /// <para><b>It does not touch the save.</b> Prefab entities are rebuilt from the game's
    /// assets on every launch, so nothing written here is persisted. Putting the sliders back
    /// to 100 restores shipped behaviour exactly, and uninstalling the mod does the same even
    /// mid-save.</para>
    ///
    /// <para><b>Several levers, because buildings do not carry their own flammability.</b>
    /// Everything the player zones has no <c>DestructibleObjectData</c> at all:
    /// <c>GetFireHazard</c> answers a hardcoded 100 for them and <c>GetStructuralIntegrity</c>
    /// falls through to per-level values on <see cref="FireConfigurationData"/>. So reaching a
    /// house means going through its zone's <c>m_FireHazardMultiplier</c>, reaching a signature
    /// building means its own component, and reaching either one's collapse speed means both
    /// the component and the configuration singleton.</para>
    ///
    /// <para><b>Originals are cached.</b> Every write is computed from the value the game
    /// shipped rather than from whatever is currently there, so dragging a slider twice does
    /// not compound - the same discipline <see cref="OriginalMeshColor"/> applies to colours.
    /// </para>
    /// </summary>
    public sealed partial class FireTuningSystem : GameSystemBase
    {
        /// <summary>Tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 64;

        /// <summary>
        /// What a zone whose authored fire hazard multiplier is zero is treated as, once the
        /// player has asked for more fire than vanilla.
        ///
        /// <para>A zero there is absolute: the building's hazard is multiplied by it and ends
        /// at zero, so the spread roll can never succeed and the building cannot burn under
        /// any circumstances. Asset packs ship zones like this - the USSW residential zones
        /// do - and it is invisible in game, because the buildings look and behave normally
        /// right up until fire reaches them and stops.</para>
        ///
        /// <para>Scaling cannot rescue it: any multiplier times zero is still zero. So when
        /// the building slider is above 100, a zero is read as this instead, which is the
        /// ordinary value a base-game zone carries. At 100 it is left alone, because asking
        /// for vanilla means asking for the game exactly as authored.</para>
        /// </summary>
        private const float kZeroZoneFallback = 1f;

        private EntityQuery m_FirePrefabQuery;
        private EntityQuery m_BuildingPrefabQuery;
        private EntityQuery m_ZonePrefabQuery;
        private EntityQuery m_SpawnablePrefabQuery;
        private EntityQuery m_FireConfigQuery;

        private PrefabSystem m_PrefabSystem;

        private NativeHashMap<Entity, FireData> m_OriginalFire;
        private NativeHashMap<Entity, DestructibleObjectData> m_OriginalDestructible;
        private NativeHashMap<Entity, float> m_OriginalZoneHazard;

        private FireConfigurationData m_OriginalFireConfig;
        private bool m_HaveOriginalFireConfig;

        private int m_AppliedBuildingSpread = -1;
        private int m_AppliedBuildingRange = -1;
        private int m_AppliedVegetationSpread = -1;
        private int m_AppliedVegetationRange = -1;
        private int m_AppliedCollapseSpeed = -1;
        private int m_AppliedFireCount = -1;
        private int m_AppliedBuildingCount = -1;
        private int m_AppliedZoneCount = -1;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // The fire event prefabs - the same set FireHazardSystem picks from when it starts
            // a fire, and the ones FireSimulationSystem reads spread numbers from.
            m_FirePrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<EventData>(),
                ComponentType.ReadWrite<FireData>());

            // Buildings that carry their own flammability: signature and service buildings
            // rather than anything zoned.
            m_BuildingPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingData>(),
                ComponentType.ReadWrite<DestructibleObjectData>());

            // Every zoned building's hazard passes through its zone's multiplier, which is
            // what makes this the lever that reaches houses.
            m_ZonePrefabQuery = GetEntityQuery(ComponentType.ReadWrite<ZonePropertiesData>());

            // Every zoned building prefab, so the zone lever's reach can be measured rather
            // than assumed.
            m_SpawnablePrefabQuery = GetEntityQuery(ComponentType.ReadOnly<SpawnableBuildingData>());

            // One singleton carrying the per-level structural integrity zoned buildings fall
            // back to when they have no DestructibleObjectData of their own.
            m_FireConfigQuery = GetEntityQuery(ComponentType.ReadWrite<FireConfigurationData>());

            m_OriginalFire = new NativeHashMap<Entity, FireData>(16, Allocator.Persistent);
            m_OriginalZoneHazard = new NativeHashMap<Entity, float>(64, Allocator.Persistent);
            m_OriginalDestructible =
                new NativeHashMap<Entity, DestructibleObjectData>(256, Allocator.Persistent);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            if (m_OriginalFire.IsCreated)
            {
                m_OriginalFire.Dispose();
            }

            if (m_OriginalDestructible.IsCreated)
            {
                m_OriginalDestructible.Dispose();
            }

            if (m_OriginalZoneHazard.IsCreated)
            {
                m_OriginalZoneHazard.Dispose();
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
            if (settings == null)
            {
                return;
            }

            int fireCount = m_FirePrefabQuery.CalculateEntityCount();
            int buildingCount = m_BuildingPrefabQuery.CalculateEntityCount();
            int zoneCount = m_ZonePrefabQuery.CalculateEntityCount();

            // Prefabs stream in over time - an asset pack finishing its load is the usual
            // case - so a count that has moved means entries that have never been retuned,
            // not only a slider the player just dragged.
            bool countsMoved = fireCount != m_AppliedFireCount
                            || buildingCount != m_AppliedBuildingCount
                            || zoneCount != m_AppliedZoneCount;

            bool slidersMoved = settings.BuildingSpread != m_AppliedBuildingSpread
                             || settings.BuildingSpreadRange != m_AppliedBuildingRange
                             || settings.VegetationSpread != m_AppliedVegetationSpread
                             || settings.VegetationSpreadRange != m_AppliedVegetationRange
                             || settings.CollapseSpeed != m_AppliedCollapseSpeed;

            if (!countsMoved && !slidersMoved)
            {
                return;
            }

            if (countsMoved && m_AppliedFireCount >= 0)
            {
                Mod.log.Info("New prefabs appeared - retuning. Fire " + m_AppliedFireCount
                           + " -> " + fireCount + ", buildings " + m_AppliedBuildingCount
                           + " -> " + buildingCount + ", zones " + m_AppliedZoneCount
                           + " -> " + zoneCount + ".");
            }

            ApplySpread(settings);
            ApplyBuildings(settings);
            ApplyZones(settings);
            ApplyFireConfiguration(settings);

            m_AppliedBuildingSpread = settings.BuildingSpread;
            m_AppliedBuildingRange = settings.BuildingSpreadRange;
            m_AppliedVegetationSpread = settings.VegetationSpread;
            m_AppliedVegetationRange = settings.VegetationSpreadRange;
            m_AppliedCollapseSpeed = settings.CollapseSpeed;
            m_AppliedFireCount = fireCount;
            m_AppliedBuildingCount = buildingCount;
            m_AppliedZoneCount = zoneCount;
        }

        /// <summary>
        /// Rewrites how fire moves between objects, picking the slider by what the fire is
        /// meant to burn.
        ///
        /// <para>A fire event names its quarry in <see cref="FireData.m_RandomTargetType"/> -
        /// Building Fire, Forest Fire, and two that can start anywhere. Fire crossing a
        /// firebreak and fire crossing a garden fence were one number until now, which is why
        /// no single setting could ever be right for both.</para>
        /// </summary>
        private void ApplySpread(ScorchedEarthSettings settings)
        {
            float buildings = settings.BuildingSpreadFactor;
            float vegetation = settings.VegetationSpreadFactor;
            float buildingRange = settings.BuildingRangeFactor;
            float vegetationRange = settings.VegetationRangeFactor;

            NativeArray<Entity> prefabs = m_FirePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    FireData original;
                    if (!m_OriginalFire.TryGetValue(prefab, out original))
                    {
                        original = EntityManager.GetComponentData<FireData>(prefab);
                        m_OriginalFire.Add(prefab, original);
                    }

                    float factor;
                    float rangeFactor;
                    switch (original.m_RandomTargetType)
                    {
                        case EventTargetType.Building:
                            factor = buildings;
                            rangeFactor = buildingRange;
                            break;
                        case EventTargetType.WildTree:
                            factor = vegetation;
                            rangeFactor = vegetationRange;
                            break;
                        default:
                            // Lightning and accidents can start on either, so they follow
                            // whichever setting is the more permissive - a strike in a forest
                            // then behaves like a forest fire rather than ignoring the slider
                            // the player actually moved.
                            factor = math.max(buildings, vegetation);
                            rangeFactor = math.max(buildingRange, vegetationRange);
                            break;
                    }

                    FireData tuned = original;
                    tuned.m_SpreadProbability = original.m_SpreadProbability
                                              * ScorchedEarthSettings.SpreadProbabilityScale(factor);
                    tuned.m_SpreadRange = original.m_SpreadRange * rangeFactor;

                    // A spontaneous fire rolls against hazard times start probability. The
                    // building slider multiplies that hazard so buildings catch from fires
                    // nearby, which would multiply random fires by the same amount - so it is
                    // divided back out here, leaving unprompted fires exactly as often as the
                    // game intended.
                    if (original.m_RandomTargetType == EventTargetType.Building && buildings > 0f)
                    {
                        tuned.m_StartProbability = original.m_StartProbability / buildings;
                    }

                    EntityManager.SetComponentData(prefab, tuned);

                    Mod.log.Info("  " + m_PrefabSystem.GetPrefabName(prefab)
                               + " [" + original.m_RandomTargetType + "] chance x" + factor
                               + " reach x" + rangeFactor
                               + ": range " + original.m_SpreadRange + " -> " + tuned.m_SpreadRange
                               + ", probability " + original.m_SpreadProbability
                               + " -> " + tuned.m_SpreadProbability);
                }

                Mod.log.Info("Fire spread retuned: buildings " + settings.BuildingSpread
                           + "% chance / " + settings.BuildingSpreadRange + "% reach, vegetation "
                           + settings.VegetationSpread + "% chance / "
                           + settings.VegetationSpreadRange + "% reach.");
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        /// <summary>Buildings that carry their own flammability and structural integrity.</summary>
        private void ApplyBuildings(ScorchedEarthSettings settings)
        {
            float hazard = settings.BuildingSpreadFactor;
            float integrity = 1f / settings.CollapseSpeedFactor;

            NativeArray<Entity> prefabs = m_BuildingPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    DestructibleObjectData original;
                    if (!m_OriginalDestructible.TryGetValue(prefab, out original))
                    {
                        original = EntityManager.GetComponentData<DestructibleObjectData>(prefab);
                        m_OriginalDestructible.Add(prefab, original);
                    }

                    DestructibleObjectData tuned = original;
                    tuned.m_FireHazard = original.m_FireHazard * hazard;
                    tuned.m_StructuralIntegrity = original.m_StructuralIntegrity * integrity;

                    EntityManager.SetComponentData(prefab, tuned);
                }

                Mod.log.Info("Retuned " + prefabs.Length + " building prefab(s) carrying their own "
                           + "flammability: hazard x" + hazard + ", integrity x" + integrity + ".");
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        /// <summary>
        /// How readily zoned buildings catch.
        ///
        /// <para>This is the one that reaches houses. A zoned building has no fire hazard of
        /// its own - the calculation starts from a hardcoded 100 and multiplies by the zone's
        /// multiplier, so the zone is the only place its flammability can be changed from.
        /// </para>
        /// </summary>
        private void ApplyZones(ScorchedEarthSettings settings)
        {
            float hazard = settings.BuildingSpreadFactor;
            bool aboveVanilla = settings.BuildingSpread > 100;

            int rescued = 0;
            Entity firstRescued = Entity.Null;

            NativeArray<Entity> prefabs = m_ZonePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    float original;
                    if (!m_OriginalZoneHazard.TryGetValue(prefab, out original))
                    {
                        original = EntityManager.GetComponentData<ZonePropertiesData>(prefab)
                                                .m_FireHazardMultiplier;
                        m_OriginalZoneHazard.Add(prefab, original);
                    }

                    // A zone authored at zero cannot be scaled into flammability, so above
                    // vanilla it is read as an ordinary zone instead. Anything non-zero keeps
                    // its relative weighting.
                    float baseValue = original;
                    if (original <= 0f && aboveVanilla)
                    {
                        baseValue = kZeroZoneFallback;
                        rescued++;

                        if (firstRescued == Entity.Null)
                        {
                            firstRescued = prefab;
                        }
                    }

                    ZonePropertiesData data = EntityManager.GetComponentData<ZonePropertiesData>(prefab);
                    data.m_FireHazardMultiplier = baseValue * hazard;
                    EntityManager.SetComponentData(prefab, data);
                }

                Mod.log.Info("Zoned building flammability x" + hazard + " across "
                           + prefabs.Length + " zone prefab(s).");

                if (rescued > 0)
                {
                    Mod.log.Info("  " + rescued + " zone(s) were authored with a fire hazard "
                               + "multiplier of zero, which makes their buildings unable to burn "
                               + "at all - treated as " + kZeroZoneFallback
                               + " instead. First one: "
                               + m_PrefabSystem.GetPrefabName(firstRescued) + ".");
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            ReportZoneReach();
        }

        /// <summary>
        /// Counts how many zoned building prefabs the zone lever can actually reach.
        ///
        /// <para>A building's hazard multiplier is read from the zone its own
        /// <c>SpawnableBuildingData.m_ZonePrefab</c> points at. If that particular zone
        /// carries no <c>ZonePropertiesData</c>, the game skips the multiply entirely and the
        /// building keeps vanilla flammability no matter what this mod writes.</para>
        /// </summary>
        private void ReportZoneReach()
        {
            int reached = 0;
            int missed = 0;
            Entity firstMissed = Entity.Null;

            NativeArray<Entity> prefabs = m_SpawnablePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity zone = EntityManager.GetComponentData<SpawnableBuildingData>(prefabs[i])
                                               .m_ZonePrefab;

                    if (zone != Entity.Null && EntityManager.HasComponent<ZonePropertiesData>(zone))
                    {
                        reached++;
                    }
                    else
                    {
                        missed++;
                        if (firstMissed == Entity.Null)
                        {
                            firstMissed = prefabs[i];
                        }
                    }
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            if (missed > 0)
            {
                Mod.log.Warn("Zone lever reaches " + reached + " of " + (reached + missed)
                           + " zoned building prefabs. " + missed
                           + " point at a zone with no ZonePropertiesData and keep vanilla "
                           + "flammability - first one: "
                           + m_PrefabSystem.GetPrefabName(firstMissed) + ".");
            }
        }

        /// <summary>
        /// How long zoned buildings stand once alight.
        ///
        /// <para>Their structural integrity is not on the building - it is five per-level
        /// numbers on the fire configuration singleton, which is what a zoned building falls
        /// back to when it has no <c>DestructibleObjectData</c>. Integrity divides the damage
        /// a fire does each tick, so a faster collapse means a smaller number.</para>
        /// </summary>
        private void ApplyFireConfiguration(ScorchedEarthSettings settings)
        {
            if (m_FireConfigQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            float integrity = 1f / settings.CollapseSpeedFactor;

            NativeArray<Entity> prefabs = m_FireConfigQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    if (!m_HaveOriginalFireConfig)
                    {
                        m_OriginalFireConfig = EntityManager.GetComponentData<FireConfigurationData>(prefab);
                        m_HaveOriginalFireConfig = true;
                    }

                    FireConfigurationData tuned =
                        EntityManager.GetComponentData<FireConfigurationData>(prefab);

                    tuned.m_DefaultStructuralIntegrity =
                        m_OriginalFireConfig.m_DefaultStructuralIntegrity * integrity;
                    tuned.m_BuildingStructuralIntegrity =
                        m_OriginalFireConfig.m_BuildingStructuralIntegrity * integrity;
                    tuned.m_StructuralIntegrityLevel1 =
                        m_OriginalFireConfig.m_StructuralIntegrityLevel1 * integrity;
                    tuned.m_StructuralIntegrityLevel2 =
                        m_OriginalFireConfig.m_StructuralIntegrityLevel2 * integrity;
                    tuned.m_StructuralIntegrityLevel3 =
                        m_OriginalFireConfig.m_StructuralIntegrityLevel3 * integrity;
                    tuned.m_StructuralIntegrityLevel4 =
                        m_OriginalFireConfig.m_StructuralIntegrityLevel4 * integrity;
                    tuned.m_StructuralIntegrityLevel5 =
                        m_OriginalFireConfig.m_StructuralIntegrityLevel5 * integrity;

                    EntityManager.SetComponentData(prefab, tuned);
                }

                Mod.log.Info("Building collapse speed " + settings.CollapseSpeed
                           + "% (structural integrity x" + integrity + ").");
            }
            finally
            {
                prefabs.Dispose();
            }
        }

        [Preserve]
        public FireTuningSystem()
        {
        }
    }
}
