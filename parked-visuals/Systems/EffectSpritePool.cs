using System.Collections.Generic;
using Game.Common;
using Game.Effects;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Mathematics;
using EditorContainer = Game.Tools.EditorContainer;
using Transform = Game.Objects.Transform;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// A recycled pool of entities that each host exactly one visual effect.
    ///
    /// <para>The game already supports a standalone effect object: an entity carrying
    /// <see cref="EditorContainer"/> plus an <see cref="EnabledEffect"/> buffer. The
    /// container names the effect prefab and, crucially, carries a <em>per-instance</em>
    /// scale and intensity, which is what makes it possible to resize individual flame
    /// and smoke sprites. This is the same mechanism the in-game editor uses to place
    /// effects by hand; nothing here is a private back door.</para>
    ///
    /// <para>Entities are reused between updates. Rebuilding the visuals is a matter of
    /// writing new transforms and marking the entity with <see cref="EffectsUpdated"/>,
    /// which is far cheaper than the structural change of creating and destroying
    /// entities. Surplus entities are only destroyed once the pool has been oversized
    /// for a while, so a flickering fire does not cause allocation churn.</para>
    ///
    /// <para><b>KNOWN UNSAFE - the two features that use this pool default to off.</b>
    ///
    /// Creating a sprite crashes the game: an access violation reading address 0x1C - a
    /// null-pointer dereference - inside lib_burst_generated.dll, i.e. a Burst-compiled job.
    /// A Burst job with safety checks disabled is dereferencing a component this entity does
    /// not carry.</para>
    ///
    /// <para>The entity below is hand-assembled to be the minimum the effect pipeline appeared
    /// to need. The game never produces that combination: its own editor effect container is a
    /// full object entity (<c>Object</c>, <c>Static</c>, <c>CullingInfo</c>, <c>SubObject</c>,
    /// <c>PseudoRandomSeed</c>) built by GenerateObjectsSystem from a real object prefab.
    /// Adding those is not a fix either - <c>CullingInfo</c> plus an update tag is exactly
    /// PreCullingSystem's entry condition, which would hand the entity to the batch renderer,
    /// and its PrefabRef points at an effect prefab with no mesh data behind it.</para>
    ///
    /// <para>The likely way out is to stop creating entities at all: append extra entries to a
    /// burning prefab's own <c>Effect</c> buffer instead. Appending does not disturb the
    /// indices live instances already hold (unlike removal, which caused an earlier crash), and
    /// it lets the game's own machinery do the instancing, culling and transforms. Offsets
    /// along the footprint line are a prefab-level property, so a fire front can still be laid
    /// out that way.</para>
    /// </summary>
    public sealed class EffectSpritePool
    {
        /// <summary>Surplus entries kept around before the pool shrinks, as a fraction.</summary>
        private const float kShrinkSlack = 0.34f;

        /// <summary>Consecutive undersized updates before surplus entries are released.</summary>
        private const int kShrinkDelay = 8;

        private readonly EntityManager m_EntityManager;
        private readonly int m_Role;
        private readonly List<Entity> m_Sprites = new List<Entity>();

        /// <summary>
        /// Structural changes are queued rather than applied immediately. Adding a tag
        /// component through the EntityManager forces every running job to complete; routing
        /// them through a barrier's command buffer batches the whole rebuild into one
        /// structural change at a point the game has already chosen to sync.
        /// </summary>
        private EntityCommandBuffer m_Commands;

        private int m_Active;
        private int m_ShrinkCounter;

        public EffectSpritePool(EntityManager entityManager, int role)
        {
            m_EntityManager = entityManager;
            m_Role = role;
        }

        /// <summary>Sprites written since the last <see cref="Begin"/>.</summary>
        public int ActiveCount
        {
            get { return m_Active; }
        }

        /// <summary>Entities currently held, active or spare.</summary>
        public int PooledCount
        {
            get { return m_Sprites.Count; }
        }

        /// <summary>Starts a rebuild. Every sprite still wanted must be re-submitted.</summary>
        /// <param name="commands">Command buffer the rebuild's structural changes go into.</param>
        public void Begin(EntityCommandBuffer commands)
        {
            m_Commands = commands;
            m_Active = 0;
        }

        /// <summary>
        /// Places one sprite. Reuses a pooled entity when one is spare, otherwise creates one.
        /// </summary>
        /// <param name="effectPrefab">Effect prefab entity from the fire effect catalog.</param>
        /// <param name="position">World position for the sprite.</param>
        /// <param name="rotation">World rotation for the sprite.</param>
        /// <param name="scale">Per-axis scale; this is what resizes the sprite.</param>
        /// <param name="intensity">Per-instance strength, 0 to 1.</param>
        /// <param name="source">Object the sprite belongs to, for diagnostics.</param>
        public void Submit(Entity effectPrefab, float3 position, quaternion rotation, float3 scale,
                           float intensity, Entity source)
        {
            if (effectPrefab == Entity.Null)
            {
                return;
            }

            Entity sprite;
            if (m_Active < m_Sprites.Count)
            {
                sprite = m_Sprites[m_Active];
                if (!m_EntityManager.Exists(sprite))
                {
                    sprite = Create();
                    m_Sprites[m_Active] = sprite;
                }
            }
            else
            {
                sprite = Create();
                m_Sprites.Add(sprite);
            }

            m_Active++;

            m_EntityManager.SetComponentData(sprite, new Transform(position, rotation));
            m_EntityManager.SetComponentData(sprite, new EditorContainer
            {
                m_Prefab = effectPrefab,
                m_Scale = scale,
                m_Intensity = intensity,

                // -1 means "no animation group", which keeps the effect out of the
                // animation-curve path and stops it being treated as animated.
                m_GroupIndex = -1,
            });
            m_EntityManager.SetComponentData(sprite, new PrefabRef(effectPrefab));
            m_EntityManager.SetComponentData(sprite, new ScorchedEarthEffect
            {
                m_Source = source,
                m_Role = m_Role,
            });

            MarkUpdated(sprite);
        }

        /// <summary>
        /// Tells EffectControlSystem to re-evaluate this sprite, which in turn makes
        /// EffectTransformSystem pick up the new transform, scale and intensity. The game's
        /// own clean-up system strips the tag again at the end of the frame.
        /// </summary>
        private void MarkUpdated(Entity sprite)
        {
            if (!m_EntityManager.HasComponent<EffectsUpdated>(sprite))
            {
                m_Commands.AddComponent<EffectsUpdated>(sprite);
            }
        }

        /// <summary>
        /// Ends a rebuild. Unused entities are hidden immediately and released after the
        /// pool has stayed oversized for <see cref="kShrinkDelay"/> updates.
        /// </summary>
        public void End()
        {
            int surplus = m_Sprites.Count - m_Active;
            if (surplus <= 0)
            {
                m_ShrinkCounter = 0;
                return;
            }

            // Hide the surplus now: intensity zero stops the effect being drawn without a
            // structural change, so a fire that shrinks and grows again costs nothing.
            for (int i = m_Active; i < m_Sprites.Count; i++)
            {
                Entity sprite = m_Sprites[i];
                if (!m_EntityManager.Exists(sprite))
                {
                    continue;
                }

                EditorContainer container = m_EntityManager.GetComponentData<EditorContainer>(sprite);
                if (container.m_Intensity == 0f)
                {
                    continue;
                }

                container.m_Intensity = 0f;
                container.m_Scale = float3.zero;
                m_EntityManager.SetComponentData(sprite, container);

                MarkUpdated(sprite);
            }

            int keep = m_Active + (int)math.ceil(m_Active * kShrinkSlack);
            if (m_Sprites.Count <= keep)
            {
                m_ShrinkCounter = 0;
                return;
            }

            if (++m_ShrinkCounter < kShrinkDelay)
            {
                return;
            }

            m_ShrinkCounter = 0;
            for (int i = m_Sprites.Count - 1; i >= keep; i--)
            {
                Release(m_Sprites[i]);
                m_Sprites.RemoveAt(i);
            }
        }

        /// <summary>
        /// Retires one sprite.
        ///
        /// The entity is tagged <see cref="Deleted"/> rather than destroyed outright. The
        /// effect systems keep their own index of enabled effects keyed by owner entity, and
        /// that tag is how they are told to drop an owner: EffectControlSystem sees it, marks
        /// the effect deleted, and CompleteEnabledSystem removes the bookkeeping. The game's
        /// clean-up system then destroys the entity at the end of the frame. Destroying it
        /// here instead would leave those systems holding a reference to a dead entity.
        /// </summary>
        private void Release(Entity sprite)
        {
            if (!m_EntityManager.Exists(sprite) || m_EntityManager.HasComponent<Deleted>(sprite))
            {
                return;
            }

            m_Commands.AddComponent<Deleted>(sprite);
        }

        /// <summary>
        /// Forgets every pooled entity without touching it.
        ///
        /// Used after a save is loaded: sprites carry <c>EffectInstance</c>, so the game has
        /// already destroyed them as part of clearing the old world, and the handles this
        /// pool is holding refer to nothing.
        /// </summary>
        public void Forget()
        {
            m_Sprites.Clear();
            m_Active = 0;
            m_ShrinkCounter = 0;
        }

        /// <summary>
        /// Retires every entity in the pool. Called when the mod or the game world shuts down.
        /// </summary>
        /// <param name="commands">
        /// Command buffer to retire through. When null the entities are destroyed directly,
        /// which is only safe during world teardown - there is no later frame in which the
        /// effect systems could observe the dangling owners.
        /// </param>
        public void Dispose(EntityCommandBuffer? commands)
        {
            for (int i = 0; i < m_Sprites.Count; i++)
            {
                Entity sprite = m_Sprites[i];
                if (!m_EntityManager.Exists(sprite))
                {
                    continue;
                }

                if (commands.HasValue)
                {
                    m_Commands = commands.Value;
                    Release(sprite);
                }
                else
                {
                    m_EntityManager.DestroyEntity(sprite);
                }
            }

            m_Sprites.Clear();
            m_Active = 0;
            m_ShrinkCounter = 0;
        }

        /// <summary>
        /// Builds one effect container entity.
        ///
        /// <para>The component set is deliberately minimal. In particular it carries no
        /// <c>Static</c>, <c>Object</c> or <c>CullingInfo</c>: without those the effect is
        /// distance-culled from its transform bounds and updated only when marked, which is
        /// both correct and the cheapest of the available paths.</para>
        ///
        /// <para><c>EffectInstance</c> is what marks the entity as a transient visual. The
        /// game excludes those from saves and clears them on load, which is exactly the
        /// lifetime these sprites want - they are always rebuilt from charring and fire
        /// state, so persisting them would be wrong as well as wasteful.</para>
        /// </summary>
        private Entity Create()
        {
            Entity sprite = m_EntityManager.CreateEntity(
                ComponentType.ReadWrite<EffectInstance>(),
                ComponentType.ReadWrite<Transform>(),
                ComponentType.ReadWrite<EditorContainer>(),
                ComponentType.ReadWrite<PrefabRef>(),
                ComponentType.ReadWrite<ScorchedEarthEffect>());

            m_EntityManager.AddBuffer<EnabledEffect>(sprite);
            return sprite;
        }
    }
}
