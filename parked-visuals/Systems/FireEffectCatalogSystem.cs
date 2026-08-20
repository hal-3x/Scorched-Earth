using System;
using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Stops the vanilla fire and fire-smoke visuals from spawning, and publishes a private
    /// copy of each effect for the mod to draw with.
    ///
    /// <para><b>Suppression.</b> Every fire effect prefab gets the same condition flag set in
    /// both <c>m_RequiredFlags</c> and <c>m_ForbiddenFlags</c>. The game's condition test
    /// fails an effect when a required flag is absent <i>or</i> a forbidden flag is present,
    /// so a flag in both lists can never be satisfied and the effect is never enabled. This
    /// is a two-field write per effect - eight effects in total - that the game re-evaluates
    /// on its normal schedule, and it unwinds by writing the original values back.</para>
    ///
    /// <para><b>What this deliberately does not do.</b> An earlier version removed fire
    /// entries from the ~9,000 owner prefabs' <see cref="Effect"/> buffers. That crashes the
    /// game. Live instances hold an <c>EnabledEffect.m_EffectIndex</c> into that buffer, and
    /// while <c>EffectControlSystem</c> range-checks a stale index, <c>EffectTransformSystem</c>
    /// does not - it indexes the buffer directly. Shortening a buffer under a live instance
    /// therefore produces an out-of-bounds read inside a Burst job with safety checks
    /// disabled: an access violation and a straight crash to desktop with no managed stack
    /// trace. Owner prefabs are now left strictly alone.</para>
    ///
    /// <para><b>Republication.</b> The mod cannot draw with the prefabs it just disabled, so
    /// it creates a private entity per effect carrying a clean <see cref="EffectData"/> and a
    /// copy of the original's <see cref="VFXData"/>. The renderer keys off
    /// <c>VFXData.m_Index</c>, so the copy draws through exactly the same visual effect while
    /// answering to its own, unconditional, enable rules.</para>
    ///
    /// <para>Only whole-name matches against known fire assets are touched. Industrial smoke,
    /// steam and water vapour keep their vanilla wiring, so smokestacks are unaffected.</para>
    /// </summary>
    public sealed partial class FireEffectCatalogSystem : GameSystemBase
    {
        /// <summary>
        /// Flame visual assets, ordered small to large.
        ///
        /// Names are matched through <see cref="Normalize"/>, which is case-insensitive and
        /// ignores a trailing "VFX". The game is inconsistent about both: the asset-bundle
        /// entry is "FireBigVFX" while the loaded object is "FireBig", and the smoke asset is
        /// "smokeFromFire" with a lowercase initial.
        /// </summary>
        private static readonly string[] kFlameAssets =
        {
            "FireTiny", "FireSmall", "FireMedium", "FireMovingMedium", "FireBig",
        };

        /// <summary>
        /// Smoke visual assets that belong to fire, not to industry.
        ///
        /// "smokeFactory" and the water-vapour assets are deliberately absent: matching is by
        /// whole normalised name, never by substring, so industrial smoke cannot be caught by
        /// accident.
        /// </summary>
        private static readonly string[] kSmokeAssets =
        {
            "SmokeFire", "SmokeFromFire",
        };

        /// <summary>
        /// Fire visuals suppressed but not redrawn. Both sit at the vanilla point-source
        /// position and would double up with the fire front the mod draws instead.
        /// </summary>
        private static readonly string[] kSuppressOnlyAssets =
        {
            "FireEmbers", "CarBurn",
        };

        /// <summary>
        /// The condition used to disable a vanilla fire effect. Any flag works - it is placed
        /// in both the required and forbidden sets, which is unsatisfiable either way - and
        /// <c>OnFire</c> is chosen because it states the intent in the data itself.
        /// </summary>
        private const EffectConditionFlags kBlockingFlag = EffectConditionFlags.OnFire;

        /// <summary>One usable effect plus the budget its renderer allows.</summary>
        public struct EffectRef
        {
            /// <summary>Mod-owned entity a sprite's container points at.</summary>
            public Entity m_Prefab;

            /// <summary>The vanilla effect prefab it was copied from, for diagnostics.</summary>
            public Entity m_SourcePrefab;

            /// <summary>Simultaneous instances the renderer can draw for this effect.</summary>
            public int m_MaxCount;

            /// <summary>Rank within its role: 0 is the smallest visual, higher is bigger.</summary>
            public int m_Rank;
        }

        private readonly List<EffectRef> m_Flames = new List<EffectRef>();
        private readonly List<EffectRef> m_Smokes = new List<EffectRef>();

        /// <summary>Vanilla effect prefabs whose conditions were rewritten, with the originals.</summary>
        private readonly Dictionary<Entity, EffectData> m_OriginalEffectData = new Dictionary<Entity, EffectData>();

        /// <summary>Entities this system created, destroyed when it unwinds.</summary>
        private readonly List<Entity> m_DrawPrefabs = new List<Entity>();

        /// <summary>Vanilla effect prefabs to disable, gathered during discovery.</summary>
        private readonly List<Entity> m_ToSuppress = new List<Entity>();

        /// <summary>
        /// Effects blocked but not redrawn - embers and the car-burn visual. Tracked apart from
        /// the redrawn ones so their suppression can follow the fire-front setting.
        /// </summary>
        private readonly List<Entity> m_SuppressOnly = new List<Entity>();

        private EntityQuery m_VFXPrefabQuery;
        private PrefabSystem m_PrefabSystem;

        private bool m_Applied;

        /// <summary>Discovery attempts so far, used to report a persistent failure exactly once.</summary>
        private int m_Attempts;

        /// <summary>Attempts before a failure is worth reporting - assets load asynchronously.</summary>
        private const int kReportAfterAttempts = 600;

        /// <summary>Cap on asset names listed in the failure report.</summary>
        private const int kMaxReportedAssets = 40;

        /// <summary>True once fire effects were found, disabled, and republished.</summary>
        public bool Ready
        {
            get { return m_Applied && m_Flames.Count > 0; }
        }

        /// <summary>Flame effects, smallest first. Empty until <see cref="Ready"/>.</summary>
        public IReadOnlyList<EffectRef> Flames
        {
            get { return m_Flames; }
        }

        /// <summary>Fire-smoke effects, smallest first. May be empty even when ready.</summary>
        public IReadOnlyList<EffectRef> Smokes
        {
            get { return m_Smokes; }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Every effect prefab that renders through the VFX system. PrefabData keeps this
            // to real prefabs, which also stops the mod's own copies being rediscovered.
            m_VFXPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<VFXData>(),
                ComponentType.ReadWrite<EffectData>(),
                ComponentType.ReadOnly<PrefabData>());

            // Deliberately no RequireForUpdate: "the query is empty" is the failure mode most
            // worth reporting, and gating on it would suppress the report along with the work.
            // Once applied, OnUpdate costs one bool check.
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;

            if (m_Applied)
            {
                SyncDrawPrefabs();

                if (settings != null)
                {
                    SyncSuppression(settings);
                }

                return;
            }

            m_Attempts++;

            if (m_VFXPrefabQuery.IsEmptyIgnoreFilter)
            {
                if (m_Attempts == kReportAfterAttempts)
                {
                    Mod.log.Warn(
                        "No VFX effect prefabs are loaded yet, so fire visuals are still vanilla. "
                      + "This is expected in the main menu; the catalog keeps retrying and will "
                      + "report again once it succeeds.");
                }

                return;
            }

            if (!DiscoverEffects())
            {
                // Found the effects but the renderer has not claimed them yet. Try again next
                // frame rather than applying half the change.
                return;
            }

            if (m_ToSuppress.Count == 0)
            {
                if (m_Attempts == kReportAfterAttempts)
                {
                    ReportDiscoveryFailure();
                }

                return;
            }

            CacheOriginalEffectData();

            if (settings != null)
            {
                SyncSuppression(settings);
            }

            m_Applied = true;
            Mod.log.Info(
                "Fire effect catalog ready after " + m_Attempts + " attempt(s). "
              + "Flames: " + Describe(m_Flames) + ". Smoke: " + Describe(m_Smokes) + ". "
              + "Vanilla fire effects disabled: " + m_OriginalEffectData.Count + " (owner prefabs untouched).");
        }

        /// <summary>
        /// Matches loaded VFX effect prefabs against the known fire assets and creates the
        /// mod's private copy of each one it intends to draw.
        /// </summary>
        /// <returns>False if the renderer has not initialised yet and this must be retried.</returns>
        private bool DiscoverEffects()
        {
            Reset();

            NativeArray<Entity> entities = m_VFXPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];

                    EffectPrefab prefab;
                    if (!m_PrefabSystem.TryGetPrefab(entity, out prefab) || prefab == null)
                    {
                        continue;
                    }

                    VFX vfx = prefab.GetComponent<VFX>();
                    if (vfx == null || vfx.m_Effect == null)
                    {
                        continue;
                    }

                    string asset = vfx.m_Effect.name;
                    string prefabName = prefab.name;

                    int flameRank = IndexOf(kFlameAssets, asset);
                    int smokeRank = IndexOf(kSmokeAssets, asset);
                    bool suppressOnly = IndexOf(kSuppressOnlyAssets, asset) >= 0;

                    if (flameRank < 0 && smokeRank < 0 && !suppressOnly)
                    {
                        continue;
                    }

                    m_ToSuppress.Add(entity);

                    if (suppressOnly)
                    {
                        m_SuppressOnly.Add(entity);
                        Mod.Verbose(() => "Suppressing vanilla effect '" + prefabName + "' (" + asset + ").");
                        continue;
                    }

                    // VFXData carries the renderer's instance budget and its slot index, and
                    // both are only filled in once VFXSystem has initialised. A zero budget
                    // means it has not run yet, so the copy would point at nothing.
                    VFXData vfxData = EntityManager.GetComponentData<VFXData>(entity);
                    if (vfxData.m_MaxCount <= 0)
                    {
                        Mod.Verbose(() => "Renderer has not claimed '" + prefabName + "' yet; retrying.");
                        Reset();
                        return false;
                    }

                    Entity drawPrefab = CreateDrawPrefab(entity, vfxData);

                    EffectRef reference = new EffectRef
                    {
                        m_Prefab = drawPrefab,
                        m_SourcePrefab = entity,
                        m_MaxCount = vfxData.m_MaxCount,
                        m_Rank = flameRank >= 0 ? flameRank : smokeRank,
                    };

                    if (flameRank >= 0)
                    {
                        m_Flames.Add(reference);
                    }
                    else
                    {
                        m_Smokes.Add(reference);
                    }

                    int budget = vfxData.m_MaxCount;
                    Mod.Verbose(() => "Republished '" + prefabName + "' (" + asset + ") max " + budget + ".");
                }
            }
            finally
            {
                entities.Dispose();
            }

            m_Flames.Sort(CompareRank);
            m_Smokes.Sort(CompareRank);

            if (m_Flames.Count == 0)
            {
                Reset();
                return true;
            }

            return true;
        }

        /// <summary>
        /// Builds the mod's private stand-in for one vanilla effect: the same visual, with no
        /// conditions attached.
        ///
        /// <para>It carries only what the effect pipeline reads: no <c>RandomTransformData</c>
        /// or light/audio components, so the enable path never takes a branch that would look
        /// for them.</para>
        ///
        /// <para><b>The <c>Disabled</c> tag is load-bearing.</b> <c>VFXSystem.Initialize</c>
        /// walks <em>every</em> entity carrying <see cref="VFXData"/> - its query has no
        /// <c>PrefabData</c> filter - and calls <c>PrefabSystem.GetPrefab&lt;EffectPrefab&gt;</c>
        /// on each one. For an entity with no managed prefab behind it that returns null and
        /// the next line throws. Worse, it throws before the initialised flag is set, so the
        /// system retries every frame, never populates its effect table, and every VFX in the
        /// game stops rendering. It is not a one-time risk either: <c>PreDeserialize</c> clears
        /// that flag on every save load, so initialisation runs again each time a city is
        /// loaded. Unity excludes <c>Disabled</c> entities from queries unless a query opts in
        /// with <c>IncludeDisabledEntities</c>, and CS2 never does - so this tag keeps the copy
        /// invisible to every game query while direct component lookups, which is all the
        /// effect pipeline uses on a prefab, keep working.</para>
        /// </summary>
        private Entity CreateDrawPrefab(Entity source, VFXData vfxData)
        {
            EffectData sourceData = EntityManager.GetComponentData<EffectData>(source);

            Entity drawPrefab = EntityManager.CreateEntity();
            EntityManager.AddComponent<Disabled>(drawPrefab);

            EntityManager.AddComponentData(drawPrefab, new EffectData
            {
                m_Archetype = sourceData.m_Archetype,

                // No conditions: the mod decides when its own sprites are shown.
                m_Flags = default(EffectCondition),

                // The mod's containers carry no CullingInfo, so owner culling would reject
                // them outright. Distance culling still applies via their transform bounds.
                m_OwnerCulling = false,
            });

            // Same renderer slot as the original, so this draws the identical visual effect.
            EntityManager.AddComponentData(drawPrefab, vfxData);

            m_DrawPrefabs.Add(drawPrefab);
            return drawPrefab;
        }

        /// <summary>Remembers each vanilla effect's original conditions so they can be restored.</summary>
        private void CacheOriginalEffectData()
        {
            for (int i = 0; i < m_ToSuppress.Count; i++)
            {
                Entity prefab = m_ToSuppress[i];

                if (!m_OriginalEffectData.ContainsKey(prefab))
                {
                    m_OriginalEffectData[prefab] = EntityManager.GetComponentData<EffectData>(prefab);
                }
            }
        }

        /// <summary>
        /// Disables exactly those vanilla fire effects the mod is currently replacing.
        ///
        /// <para>Suppression follows the features that are switched on. Blanket suppression
        /// would mean that turning off fire fronts leaves fires with no flames at all rather
        /// than the vanilla ones - the mod would have taken the visuals away without putting
        /// anything back. A disabled feature hands its effect straight back to the game.</para>
        ///
        /// <para>Re-evaluated every update so the options screen takes effect immediately.
        /// It is a handful of comparisons and only writes when something actually differs.</para>
        /// </summary>
        private void SyncSuppression(ScorchedEarthSettings settings)
        {
            bool blockFlames = settings.FireLines;
            bool blockSmoke = settings.SmolderAreas;

            for (int i = 0; i < m_Flames.Count; i++) SetBlocked(m_Flames[i].m_SourcePrefab, blockFlames);
            for (int i = 0; i < m_Smokes.Count; i++) SetBlocked(m_Smokes[i].m_SourcePrefab, blockSmoke);

            // Embers and the car-burn effect are part of the point-source flame visual, so they
            // follow the fire fronts rather than the smoke.
            for (int i = 0; i < m_SuppressOnly.Count; i++) SetBlocked(m_SuppressOnly[i], blockFlames);
        }

        /// <summary>
        /// Blocks or restores one vanilla effect.
        ///
        /// Blocking places the same flag in both the required and forbidden sets: the game's
        /// test fails an effect when a required flag is absent <i>or</i> a forbidden flag is
        /// present, so a flag in both can never be satisfied. Restoring writes the original
        /// conditions back verbatim. Owner prefabs are never touched - see the class notes.
        /// </summary>
        private void SetBlocked(Entity prefab, bool blocked)
        {
            EffectData original;
            if (!EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<EffectData>(prefab)
                || !m_OriginalEffectData.TryGetValue(prefab, out original))
            {
                return;
            }

            EffectData desired = original;
            if (blocked)
            {
                desired.m_Flags.m_RequiredFlags |= kBlockingFlag;
                desired.m_Flags.m_ForbiddenFlags |= kBlockingFlag;
            }

            EffectData current = EntityManager.GetComponentData<EffectData>(prefab);
            if (current.m_Flags.m_RequiredFlags == desired.m_Flags.m_RequiredFlags
                && current.m_Flags.m_ForbiddenFlags == desired.m_Flags.m_ForbiddenFlags)
            {
                return;
            }

            EntityManager.SetComponentData(prefab, desired);
        }

        /// <summary>
        /// Keeps each copy's <see cref="VFXData"/> in step with the effect it mirrors.
        ///
        /// <para>That component holds the renderer's slot index, and the renderer reassigns it
        /// from scratch every time it initialises - which happens again on every save load,
        /// not just at startup. A copy holding a stale index would draw the wrong effect, or
        /// index past the end of the renderer's table. Re-reading the source is a handful of
        /// component fetches, so it is simply done every update rather than trying to detect
        /// when a re-initialisation happened.</para>
        /// </summary>
        private void SyncDrawPrefabs()
        {
            SyncDrawPrefabs(m_Flames);
            SyncDrawPrefabs(m_Smokes);
        }

        private void SyncDrawPrefabs(List<EffectRef> effects)
        {
            for (int i = 0; i < effects.Count; i++)
            {
                EffectRef reference = effects[i];

                if (!EntityManager.Exists(reference.m_SourcePrefab) || !EntityManager.Exists(reference.m_Prefab))
                {
                    continue;
                }

                VFXData source = EntityManager.GetComponentData<VFXData>(reference.m_SourcePrefab);

                // A zero budget means the renderer is mid-initialisation and has not claimed
                // the effect yet; its numbers are not usable until it has.
                if (source.m_MaxCount <= 0)
                {
                    continue;
                }

                VFXData current = EntityManager.GetComponentData<VFXData>(reference.m_Prefab);
                if (current.m_Index == source.m_Index && current.m_MaxCount == source.m_MaxCount)
                {
                    continue;
                }

                EntityManager.SetComponentData(reference.m_Prefab, source);

                reference.m_MaxCount = source.m_MaxCount;
                effects[i] = reference;

                Mod.Verbose(() => "Re-synced effect copy to renderer slot " + source.m_Index + ".");
            }
        }

        /// <summary>Drops discovery results and destroys any copies made during it.</summary>
        private void Reset()
        {
            for (int i = 0; i < m_DrawPrefabs.Count; i++)
            {
                if (EntityManager.Exists(m_DrawPrefabs[i]))
                {
                    EntityManager.DestroyEntity(m_DrawPrefabs[i]);
                }
            }

            m_DrawPrefabs.Clear();
            m_Flames.Clear();
            m_Smokes.Clear();
            m_ToSuppress.Clear();
            m_SuppressOnly.Clear();
        }

        private static int CompareRank(EffectRef a, EffectRef b)
        {
            return a.m_Rank.CompareTo(b.m_Rank);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            RestoreVanilla();
            base.OnDestroy();
        }

        /// <summary>Puts every edit back, so disabling the mod leaves no trace.</summary>
        public void RestoreVanilla()
        {
            foreach (KeyValuePair<Entity, EffectData> pair in m_OriginalEffectData)
            {
                if (EntityManager.Exists(pair.Key) && EntityManager.HasComponent<EffectData>(pair.Key))
                {
                    EntityManager.SetComponentData(pair.Key, pair.Value);
                }
            }

            m_OriginalEffectData.Clear();
            Reset();
            m_Applied = false;
        }

        /// <summary>
        /// Renders a catalog entry list as "name(rank, max=N)", so the log shows which assets
        /// were matched and the per-effect instance budget the renderer allows each one.
        /// </summary>
        private string Describe(List<EffectRef> effects)
        {
            if (effects.Count == 0)
            {
                return "none";
            }

            List<string> parts = new List<string>(effects.Count);
            for (int i = 0; i < effects.Count; i++)
            {
                EffectPrefab prefab;
                string name = m_PrefabSystem.TryGetPrefab(effects[i].m_SourcePrefab, out prefab) && prefab != null
                    ? prefab.name
                    : "<unknown>";

                parts.Add(name + "(rank " + effects[i].m_Rank + ", max " + effects[i].m_MaxCount + ")");
            }

            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// Explains why no flame effects were found. Without this the mod just silently does
        /// nothing, which is indistinguishable from a broken install.
        /// </summary>
        private void ReportDiscoveryFailure()
        {
            int vfxPrefabs = m_VFXPrefabQuery.CalculateEntityCount();

            Mod.log.Warn(
                "Found " + vfxPrefabs + " VFX effect prefab(s) but none matched a known fire "
              + "effect, so vanilla fire is left untouched. The visual assets this mod looks "
              + "for may have been renamed by a game update. Assets seen: " + DescribeSeenAssets());
        }

        /// <summary>Lists the visual-asset names actually present, for diagnosing a rename.</summary>
        private string DescribeSeenAssets()
        {
            List<string> names = new List<string>();

            NativeArray<Entity> entities = m_VFXPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length && names.Count < kMaxReportedAssets; i++)
                {
                    EffectPrefab prefab;
                    if (!m_PrefabSystem.TryGetPrefab(entities[i], out prefab) || prefab == null)
                    {
                        continue;
                    }

                    VFX vfx = prefab.GetComponent<VFX>();
                    names.Add(vfx != null && vfx.m_Effect != null ? vfx.m_Effect.name : "<no asset>");
                }
            }
            finally
            {
                entities.Dispose();
            }

            return string.Join(", ", names.ToArray());
        }

        /// <summary>
        /// Position of <paramref name="value"/> in <paramref name="names"/>, comparing
        /// normalised names, or -1. The position is the effect's size rank.
        /// </summary>
        private static int IndexOf(string[] names, string value)
        {
            string normalized = Normalize(value);

            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(Normalize(names[i]), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Strips a trailing "VFX" so bundle-style names ("FireBigVFX") and runtime object
        /// names ("FireBig") compare equal. Comparison is case-insensitive at the call site,
        /// which also absorbs the inconsistent capitalisation of the smoke assets.
        /// </summary>
        private static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            if (name.Length > 3 && name.EndsWith("VFX", StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - 3);
            }

            return name;
        }

        [Preserve]
        public FireEffectCatalogSystem()
        {
        }
    }
}
