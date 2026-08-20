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
using Color = UnityEngine.Color;
using Tree = Game.Objects.Tree;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Blackens things that are on fire and keeps that soot on them after the fire is out.
    ///
    /// <para>This system only tracks how charred a thing is and marks its render batch dirty
    /// when that changes; the colours themselves are written by <see cref="CharColorSystem"/>
    /// later in the frame, after the game has rebuilt them. Recovery is therefore exact
    /// rather than approximate - dropping the mod's components lets the game recompute the
    /// original colours, which are never guessed at.</para>
    ///
    /// <para>Trees are handled here too: one that burns hard enough is switched to
    /// <see cref="TreeState.Dead"/> so it renders with the bare dead-tree mesh, and darkened
    /// on top of that so it reads as charred rather than merely dead.
    /// <see cref="RecoverySystem"/> brings it back later.</para>
    ///
    /// <para><b>Iteration.</b> Both passes walk chunks rather than entities. Component
    /// presence is uniform across a chunk, so questions like "is this a tree" or "does it
    /// already carry <see cref="Charred"/>" are answered once per chunk instead of once per
    /// entity - which matters during a spreading forest fire, when the burning query holds
    /// thousands of entities and this used to cost seven random-access lookups on each.</para>
    /// </summary>
    public sealed partial class CharringSystem : GameSystemBase
    {
        /// <summary>Colour change smaller than this is not worth dirtying a render batch for.</summary>
        private const float kRepaintThreshold = 0.02f;

        /// <summary>
        /// Char level at or above which a burned tree is killed outright.
        ///
        /// <para>Deliberately low. A tree that catches fire does not survive it, so anything
        /// past a token amount of burning kills it. The previous value of 0.3 needed a fire
        /// intensity of 50 or more, which meant a tree whose fire was put out early kept its
        /// soot but stayed a living model - a tree with black leaves rather than a dead one.
        /// The floor is not zero only so a fire that registers for a single tick at almost no
        /// intensity does not count.</para>
        /// </summary>
        private const float kTreeDeathThreshold = 0.05f;

        /// <summary>Char a fully-raging fire produces on its own, before structural damage.</summary>
        private const float kIntensitySootShare = 0.6f;

        /// <summary>
        /// Minimum char applied to a tree the fire killed.
        ///
        /// A burned tree should read as burned the moment it turns bare, rather than picking
        /// up soot only if the mod happens to catch it still alight afterwards.
        /// </summary>
        private const float kMinTreeChar = 0.6f;

        /// <summary>
        /// Char level reached at full fire intensity. Fire intensity runs 0..100, and a
        /// building that merely singes should not end up as black as a gutted one.
        /// </summary>
        private const float kIntensityToChar = 0.01f;

        /// <summary>Fastest tick rate, in simulation frames. Must be a power of two.</summary>
        private const int kBaseInterval = 4;

        private EntityQuery m_BurningQuery;
        private EntityQuery m_BurnedTreeQuery;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_Barrier;
        private UpdateThrottle m_Throttle;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<Tree> m_TreeType;
        private ComponentTypeHandle<OnFire> m_OnFireType;
        private ComponentTypeHandle<Damaged> m_DamagedType;
        private ComponentTypeHandle<Charred> m_CharredType;
        private ComponentTypeHandle<FireKilledTree> m_FireKilledType;
        private BufferTypeHandle<MeshColor> m_MeshColorType;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Everything currently on fire.
            //
            // Deliberately not filtered on MeshColor: killing a burned tree has nothing to do
            // with whether it can show a colour change, and requiring colours here would
            // silently exempt any tree without them. CharColorSystem does that filtering.
            m_BurningQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            // Trees that fire has damaged but which are no longer alight.
            //
            // Reacting only to OnFire misses trees entirely: a tree can ignite and burn out
            // between two throttled ticks, and in a spreading forest fire that is common. Fire
            // damage persists after the fire does, so it is the reliable signal. TreeGrowthSystem
            // heals damage and drops the component, so this query empties itself.
            m_BurnedTreeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Tree>(),
                    ComponentType.ReadOnly<Damaged>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<OnFire>(),
                    ComponentType.ReadOnly<FireKilledTree>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_EntityType = GetEntityTypeHandle();
            m_TreeType = GetComponentTypeHandle<Tree>();
            m_OnFireType = GetComponentTypeHandle<OnFire>(true);
            m_DamagedType = GetComponentTypeHandle<Damaged>(true);
            m_CharredType = GetComponentTypeHandle<Charred>();
            m_FireKilledType = GetComponentTypeHandle<FireKilledTree>(true);
            m_MeshColorType = GetBufferTypeHandle<MeshColor>(true);

            // No RequireForUpdate: the burned-tree pass has to run after the fires are out.
        }

        /// <summary>
        /// The game requires a power-of-two interval and reads it once, when the system is
        /// registered, so it cannot carry a user setting. This is the fastest rate the system
        /// will ever run at; <see cref="UpdateThrottle"/> applies the user's interval on top
        /// of it and reacts to setting changes immediately.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase)
        {
            return kBaseInterval;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;
            if (settings == null || (!settings.CharBuildings && !settings.CharTrees))
            {
                return;
            }

            uint elapsed;
            if (!m_Throttle.ShouldRun(m_SimulationSystem.frameIndex, settings.SafeUpdateInterval, out elapsed))
            {
                return;
            }

            if (m_BurningQuery.IsEmptyIgnoreFilter && m_BurnedTreeQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_EntityType.Update(this);
            m_TreeType.Update(this);
            m_OnFireType.Update(this);
            m_DamagedType.Update(this);
            m_CharredType.Update(this);
            m_FireKilledType.Update(this);
            m_MeshColorType.Update(this);

            EntityCommandBuffer commands = m_Barrier.CreateCommandBuffer();

            AccumulateBurning(settings, ref commands);

            if (settings.CharTrees)
            {
                CatchUpBurnedTrees(ref commands);
            }
        }

        /// <summary>Raises the char level of everything alight, in step with how hard it burns.</summary>
        private void AccumulateBurning(ScorchedEarthSettings settings, ref EntityCommandBuffer commands)
        {
            NativeArray<ArchetypeChunk> chunks = m_BurningQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    // Uniform across the chunk, so each of these is answered once rather than
                    // once per entity.
                    bool isTree = chunk.Has<Tree>(ref m_TreeType);
                    if (isTree ? !settings.CharTrees : !settings.CharBuildings)
                    {
                        continue;
                    }

                    bool hasDamaged = chunk.Has<Damaged>(ref m_DamagedType);
                    bool hasCharred = chunk.Has<Charred>(ref m_CharredType);
                    bool hasFireKilled = chunk.Has<FireKilledTree>(ref m_FireKilledType);
                    bool hasMeshColor = chunk.Has<MeshColor>(ref m_MeshColorType);

                    NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                    NativeArray<OnFire> onFires = chunk.GetNativeArray(ref m_OnFireType);
                    NativeArray<Damaged> damaged = hasDamaged
                        ? chunk.GetNativeArray(ref m_DamagedType)
                        : default(NativeArray<Damaged>);
                    NativeArray<Charred> charredArray = hasCharred
                        ? chunk.GetNativeArray(ref m_CharredType)
                        : default(NativeArray<Charred>);
                    NativeArray<Tree> trees = isTree
                        ? chunk.GetNativeArray(ref m_TreeType)
                        : default(NativeArray<Tree>);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        OnFire onFire = onFires[i];
                        if (onFire.m_Intensity <= 0f)
                        {
                            continue;
                        }

                        Entity entity = entities[i];

                        // How badly the fire has actually burned this object.
                        //
                        // The fire simulation writes its damage into the y channel, and that
                        // number is already calibrated against the object's structural
                        // integrity: it reaches 1 exactly when the object is destroyed.
                        // Charring follows it rather than integrating elapsed time, so a brief
                        // fire leaves a scorch and a long one leaves a ruin, on every object,
                        // without the mod guessing at how long fires last.
                        float burnDamage = hasDamaged ? math.saturate(damaged[i].m_Damage.y) : 0f;

                        // Soot appears as soon as something is alight, before structural
                        // damage has had time to build up.
                        float soot = math.saturate(onFire.m_Intensity * kIntensityToChar) * kIntensitySootShare;

                        float target = math.max(burnDamage, soot);

                        if (isTree)
                        {
                            Tree tree = trees[i];

                            if (target >= kTreeDeathThreshold)
                            {
                                KillTree(entity, tree, hasFireKilled, hasCharred, hasMeshColor,
                                         target, ref commands);
                            }

                            // Soot goes on the bare dead-tree model, never on living foliage.
                            // Tinting a tree that still has its leaves just turns them black,
                            // which is not what a burned tree looks like - and the death switch
                            // happens through a command buffer, so there is a short window where
                            // the tree is still the living model. Skipping it here means that
                            // window is never visible.
                            if ((tree.m_State & TreeState.Dead) == 0)
                            {
                                continue;
                            }
                        }

                        Charred charred = hasCharred ? charredArray[i] : default(Charred);

                        // Char only ever rises while something is burning; RecoverySystem is
                        // what brings it back down once the fire is out.
                        charred.m_Amount = math.max(charred.m_Amount, target);
                        charred.m_Peak = math.max(charred.m_Peak, charred.m_Amount);

                        RequestRepaint(entity, ref charred, ref commands);

                        if (hasCharred)
                        {
                            // Already present, so this is a plain value write - no need to
                            // spend a command-buffer entry on it.
                            charredArray[i] = charred;
                        }
                        else
                        {
                            commands.AddComponent(entity, charred);

                            // The colour cache lives beside the char level; CharColorSystem
                            // fills it on the first frame it sees the object. Only objects that
                            // actually carry mesh colours need one.
                            if (hasMeshColor)
                            {
                                commands.AddBuffer<OriginalMeshColor>(entity);
                            }
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Kills trees that fire damaged while the mod was not looking.
        ///
        /// This is what makes the result consistent across a spreading fire: whether a given
        /// tree happened to be alight on one of this system's ticks stops mattering, because
        /// the damage the fire left behind is checked afterwards too.
        /// </summary>
        private void CatchUpBurnedTrees(ref EntityCommandBuffer commands)
        {
            NativeArray<ArchetypeChunk> chunks = m_BurnedTreeQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    bool hasCharred = chunk.Has<Charred>(ref m_CharredType);
                    bool hasMeshColor = chunk.Has<MeshColor>(ref m_MeshColorType);

                    NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                    NativeArray<Damaged> damaged = chunk.GetNativeArray(ref m_DamagedType);
                    NativeArray<Tree> trees = chunk.GetNativeArray(ref m_TreeType);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        float burnDamage = math.saturate(damaged[i].m_Damage.y);
                        if (burnDamage < kTreeDeathThreshold)
                        {
                            continue;
                        }

                        // The query excludes FireKilledTree, so none of these carry one yet.
                        KillTree(entities[i], trees[i], false, hasCharred, hasMeshColor,
                                 burnDamage, ref commands);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Records that the char level moved far enough to be worth a repaint, and dirties the
        /// object's render batch so the game rebuilds its colours. <see cref="CharColorSystem"/>
        /// darkens them immediately afterwards, in the same frame.
        /// </summary>
        internal static void RequestRepaint(Entity entity, ref Charred charred, ref EntityCommandBuffer commands)
        {
            if (math.abs(charred.m_Amount - charred.m_AppliedAmount) < kRepaintThreshold
                && charred.m_AppliedAmount > 0f)
            {
                return;
            }

            charred.m_AppliedAmount = charred.m_Amount;
            commands.AddComponent<BatchesUpdated>(entity);
        }

        /// <summary>
        /// Darkens a colour set toward soot. Saturation is pulled down as well as value,
        /// because soot is grey-black rather than a dark version of the original hue, and a
        /// purely multiplicative darkening leaves a red building looking maroon rather than
        /// burnt.
        /// </summary>
        internal static ColorSet Char(ColorSet source, float amount)
        {
            ColorSet result = default(ColorSet);
            result.m_Channel0 = CharChannel(source.m_Channel0, amount);
            result.m_Channel1 = CharChannel(source.m_Channel1, amount);
            result.m_Channel2 = CharChannel(source.m_Channel2, amount);
            return result;
        }

        /// <summary>
        /// Darkens one colour channel toward soot.
        ///
        /// <para>The HSV conversions are done in <c>Unity.Mathematics</c> rather than through
        /// <c>Color.RGBToHSV</c>/<c>Color.HSVToRGB</c>. This is the innermost function of the
        /// only pass that runs every rendered frame, and the two helpers below are plain
        /// arithmetic on <c>float3</c> where the Unity versions go through managed calls and
        /// intermediate <c>Color</c> structs.</para>
        /// </summary>
        internal static Color CharChannel(Color source, float amount)
        {
            float t = math.saturate(amount);

            // Soot: near-black with a faint warm-grey cast, so it does not read as a flat
            // shadow against the terrain.
            float3 soot = new float3(0.055f, 0.048f, 0.045f);

            float3 hsv = RgbToHsv(new float3(source.r, source.g, source.b));

            // Desaturate first, then darken - in that order the midpoint of the fade looks
            // like ash rather than a muddy version of the original colour.
            hsv.y *= 1f - t * 0.8f;

            float3 result = math.lerp(HsvToRgb(hsv), soot, t * 0.85f);
            return new Color(result.x, result.y, result.z, source.a);
        }

        /// <summary>RGB to HSV. Arithmetic stand-in for <c>Color.RGBToHSV</c>.</summary>
        private static float3 RgbToHsv(float3 c)
        {
            float max = math.cmax(c);
            float min = math.cmin(c);
            float delta = max - min;

            float h = 0f;
            if (delta > 1e-10f)
            {
                if (max == c.x)
                {
                    h = (c.y - c.z) / delta;
                }
                else if (max == c.y)
                {
                    h = 2f + (c.z - c.x) / delta;
                }
                else
                {
                    h = 4f + (c.x - c.y) / delta;
                }

                h *= 1f / 6f;
                if (h < 0f)
                {
                    h += 1f;
                }
            }

            return new float3(h, max > 1e-10f ? delta / max : 0f, max);
        }

        /// <summary>HSV to RGB. Arithmetic stand-in for <c>Color.HSVToRGB</c>.</summary>
        private static float3 HsvToRgb(float3 hsv)
        {
            float s = math.saturate(hsv.y);
            float v = hsv.z;

            if (s <= 1e-10f)
            {
                return new float3(v, v, v);
            }

            float h = math.frac(hsv.x) * 6f;
            int sector = (int)h;
            float f = h - sector;

            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float w = v * (1f - s * (1f - f));

            switch (sector)
            {
                case 0: return new float3(v, w, p);
                case 1: return new float3(q, v, p);
                case 2: return new float3(p, v, w);
                case 3: return new float3(p, q, v);
                case 4: return new float3(w, p, v);
                default: return new float3(v, p, q);
            }
        }

        /// <summary>
        /// Switches a burned tree to the vanilla dead state, remembering what it was so it
        /// can be restored. Dead is a vanilla <see cref="TreeState"/>, so this reuses the
        /// game's existing bare-tree mesh rather than shipping a new asset.
        /// </summary>
        private void KillTree(Entity entity, Tree tree, bool hasFireKilled, bool hasCharred,
                              bool hasMeshColor, float burnAmount, ref EntityCommandBuffer commands)
        {
            if (hasFireKilled)
            {
                return;
            }

            bool alreadyDead = (tree.m_State & TreeState.Dead) != 0;

            if (!alreadyDead)
            {
                commands.AddComponent(entity, new FireKilledTree
                {
                    m_Regrowth = 0f,
                    m_OriginalGrowth = tree.m_Growth,
                    m_OriginalState = (byte)tree.m_State,
                });

                tree.m_State = TreeState.Dead;
                commands.SetComponent(entity, tree);
            }

            // Char the bare model straight away rather than waiting to catch the tree alight
            // again on a later tick - by then the fire has usually moved on, which left burned
            // trees looking merely dead instead of burned.
            StartChar(entity, hasCharred, hasMeshColor, math.max(burnAmount, kMinTreeChar), ref commands);

            // BatchesUpdated on its own is what the game's own TreeGrowthSystem adds when it
            // changes a tree's state; it makes BatchInstanceSystem re-select the sub-mesh.
            commands.AddComponent<BatchesUpdated>(entity);

            // Checked rather than passed as a lambda: a closure is allocated at the call site
            // whether or not logging is on, and this runs once per tree in a forest fire.
            if (Mod.IsVerbose)
            {
                Mod.log.Info("Fire killed tree " + entity.Index
                           + " (burn " + burnAmount.ToString("0.00") + "); switching to the dead-tree mesh.");
            }
        }

        /// <summary>Gives an object its initial char level, if it does not have one yet.</summary>
        private static void StartChar(Entity entity, bool hasCharred, bool hasMeshColor,
                                      float amount, ref EntityCommandBuffer commands)
        {
            if (hasCharred)
            {
                return;
            }

            Charred charred = default(Charred);
            charred.m_Amount = math.saturate(amount);
            charred.m_Peak = charred.m_Amount;
            charred.m_AppliedAmount = charred.m_Amount;

            commands.AddComponent(entity, charred);

            if (hasMeshColor)
            {
                commands.AddBuffer<OriginalMeshColor>(entity);
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
        public CharringSystem()
        {
        }
    }
}
