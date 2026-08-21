using Game;
using Game.Common;
using Game.Events;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Color = UnityEngine.Color;

namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Darkens the mesh colours of charred objects, in place.
    ///
    /// <para><b>Why not CustomMeshColor.</b> The obvious tool is the game's per-instance
    /// colour override, and it is what the colour picker and recolouring mods use. It cannot
    /// be used here: <c>MeshColorSystem.ApplyCustomMeshColors</c> hard-resizes the target's
    /// <c>MeshColor</c> buffer to exactly one entry, while the batch renderer indexes that
    /// buffer at <c>meshBatch.m_MeshIndex</c> with no bounds check. On anything with more
    /// than one sub-mesh colour - every tree - that reads past the end of the buffer and
    /// uploads garbage, which shows up as an object flashing a nonsense colour when the
    /// renderer next touches its colour properties.</para>
    ///
    /// <para><b>What this does instead.</b> It runs immediately after <c>MeshColorSystem</c>
    /// in the same phase and overwrites the mesh colours in place, keeping the buffer length
    /// exactly as the game built it. <c>BatchDataSystem</c> uploads whatever is in the buffer
    /// later in the frame, so the darkened colours are what reach the GPU.</para>
    ///
    /// <para>The clean colours are cached in <see cref="OriginalMeshColor"/> so darkening is
    /// computed from the original every time and can never compound. The cache is refreshed
    /// whenever the game recomputes an object's colours - detected by the buffer no longer
    /// matching what this system last wrote.</para>
    ///
    /// <para><b>Cost.</b> This is the only part of the mod that runs every rendered frame, so
    /// it is written to do as little as possible. <c>RequireForUpdate</c> keeps it switched
    /// off entirely while nothing is charred, and once something is, each entity is skipped
    /// outright unless the game rebuilt its colours or the darkening actually moved - see
    /// <see cref="Charred.m_RenderedAmount"/>. Only the survivors of those two checks pay
    /// for the colour maths.</para>
    /// </summary>
    public sealed partial class CharColorSystem : GameSystemBase
    {
        /// <summary>Colour difference above which the buffer is treated as game-recomputed.</summary>
        private const float kRecaptureEpsilon = 0.002f;

        private EntityQuery m_CharredQuery;

        // Cached rather than re-fetched every frame: each Get*TypeHandle call does safety
        // bookkeeping that is pure overhead at sixty frames a second.
        private ComponentTypeHandle<Charred> m_CharredType;
        private ComponentTypeHandle<OnFire> m_OnFireType;
        private ComponentTypeHandle<IgnitePreview> m_PreviewType;
        private BufferTypeHandle<MeshColor> m_MeshColorType;
        private BufferTypeHandle<OriginalMeshColor> m_OriginalType;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            m_CharredQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadWrite<Charred>(),
                    ComponentType.ReadWrite<MeshColor>(),
                    ComponentType.ReadWrite<OriginalMeshColor>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                },
            });

            m_CharredType = GetComponentTypeHandle<Charred>();
            m_OnFireType = GetComponentTypeHandle<OnFire>(true);
            m_PreviewType = GetComponentTypeHandle<IgnitePreview>(true);
            m_MeshColorType = GetBufferTypeHandle<MeshColor>();
            m_OriginalType = GetBufferTypeHandle<OriginalMeshColor>();

            RequireForUpdate(m_CharredQuery);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            ScorchedEarthSettings settings = Mod.ActiveSettings;
            if (settings == null)
            {
                return;
            }

            float strength = settings.CharStrengthNormalized;

            // MeshColorSystem rebuilds mesh colours from scheduled parallel jobs and leaves
            // them running - it assigns its JobHandle to Dependency rather than completing
            // it. Reading the buffers on the main thread without waiting can therefore see
            // the pre-rebuild contents, which makes the recapture check below decide nothing
            // has changed and skip the entity; the rebuilt clean colours then land on top
            // and the object renders unburnt until something else dirties it. Hovering does
            // exactly that, because it marks the object Updated every frame.
            CompleteDependency();

            m_CharredType.Update(this);
            m_OnFireType.Update(this);
            m_PreviewType.Update(this);
            m_MeshColorType.Update(this);
            m_OriginalType.Update(this);

            NativeArray<ArchetypeChunk> chunks = m_CharredQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    // Chunk-uniform, so the glow is switched off for a whole archetype at
                    // once the moment the fire component comes off. A previewed object glows
                    // on the same path without being alight, which is how the ignite tool
                    // shows what it is pointing at.
                    bool burning = chunk.Has<OnFire>(ref m_OnFireType)
                                || chunk.Has<IgnitePreview>(ref m_PreviewType);

                    NativeArray<Charred> charred = chunk.GetNativeArray(ref m_CharredType);
                    BufferAccessor<MeshColor> meshColors = chunk.GetBufferAccessor(ref m_MeshColorType);
                    BufferAccessor<OriginalMeshColor> originals = chunk.GetBufferAccessor(ref m_OriginalType);

                    for (int i = 0; i < charred.Length; i++)
                    {
                        Charred state = charred[i];

                        // Write the component back only when something actually changed.
                        if (Apply(ref state, meshColors[i], originals[i], strength, burning))
                        {
                            charred[i] = state;
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
        /// Writes the darkened colours for one object. Returns whether anything was written.
        /// </summary>
        private static bool Apply(ref Charred charred, DynamicBuffer<MeshColor> meshColors,
                                  DynamicBuffer<OriginalMeshColor> originals, float strength,
                                  bool burning)
        {
            if (meshColors.Length == 0)
            {
                return false;
            }

            // Re-capture when the game rebuilt these colours: either the buffer changed shape
            // (a tree switching growth state does this) or its contents no longer match what
            // was written last time, which only happens when MeshColorSystem has run.
            bool recapture = originals.Length != meshColors.Length
                          || !Approximately(meshColors[0].m_ColorSet.m_Channel0, charred.m_LastWritten);

            float amount = math.saturate(charred.m_Amount) * strength;

            // The glow is only honoured while the object is actually alight, so it lapses by
            // itself when OnFire comes off - nothing has to remember to clear it.
            float ember = burning ? math.saturate(charred.m_Ember) : 0f;

            // Nothing to do: the game has not touched these colours, and the darkening they
            // were produced from has not moved, so the buffer already holds exactly what
            // would be written. This is the overwhelmingly common case - char changes at most
            // once every few simulation frames, and this runs on every rendered one.
            //
            // Comparing the strength-scaled amount rather than the raw char level means a
            // change to the strength setting is picked up here too, with no extra state.
            if (!recapture && charred.m_RenderedAmount == amount && charred.m_RenderedEmber == ember)
            {
                return false;
            }

            if (recapture)
            {
                originals.ResizeUninitialized(meshColors.Length);
                for (int i = 0; i < meshColors.Length; i++)
                {
                    OriginalMeshColor original = default(OriginalMeshColor);
                    original.ColorSet = meshColors[i].m_ColorSet;
                    originals[i] = original;
                }
            }

            for (int i = 0; i < meshColors.Length; i++)
            {
                MeshColor color = meshColors[i];

                // Soot first, then the glow on top: a dead trunk that is still alight should
                // read as charred wood with fire in it, not as clean wood painted orange.
                ColorSet charred_ = CharringSystem.Char(originals[i].ColorSet, amount);
                color.m_ColorSet = ember > 0f ? CharringSystem.Ember(charred_, ember) : charred_;

                meshColors[i] = color;
            }

            charred.m_LastWritten = meshColors[0].m_ColorSet.m_Channel0;
            charred.m_RenderedAmount = amount;
            charred.m_RenderedEmber = ember;
            return true;
        }

        private static bool Approximately(Color a, Color b)
        {
            return math.abs(a.r - b.r) < kRecaptureEpsilon
                && math.abs(a.g - b.g) < kRecaptureEpsilon
                && math.abs(a.b - b.b) < kRecaptureEpsilon
                && math.abs(a.a - b.a) < kRecaptureEpsilon;
        }

        [Preserve]
        public CharColorSystem()
        {
        }
    }
}
