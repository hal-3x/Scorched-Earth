using Game;
using Game.Common;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
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
    /// </summary>
    public sealed partial class CharColorSystem : GameSystemBase
    {
        /// <summary>Colour difference above which the buffer is treated as game-recomputed.</summary>
        private const float kRecaptureEpsilon = 0.002f;

        private EntityQuery m_CharredQuery;

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

            NativeArray<ArchetypeChunk> chunks = m_CharredQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                ComponentTypeHandle<Charred> charredType = GetComponentTypeHandle<Charred>();
                BufferTypeHandle<MeshColor> meshColorType = GetBufferTypeHandle<MeshColor>();
                BufferTypeHandle<OriginalMeshColor> originalType = GetBufferTypeHandle<OriginalMeshColor>();

                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    NativeArray<Charred> charred = chunk.GetNativeArray(ref charredType);
                    BufferAccessor<MeshColor> meshColors = chunk.GetBufferAccessor(ref meshColorType);
                    BufferAccessor<OriginalMeshColor> originals = chunk.GetBufferAccessor(ref originalType);

                    for (int i = 0; i < charred.Length; i++)
                    {
                        Charred state = charred[i];
                        Apply(ref state, meshColors[i], originals[i], strength);
                        charred[i] = state;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>Writes the darkened colours for one object.</summary>
        private static void Apply(ref Charred charred, DynamicBuffer<MeshColor> meshColors,
                                  DynamicBuffer<OriginalMeshColor> originals, float strength)
        {
            if (meshColors.Length == 0)
            {
                return;
            }

            // Re-capture when the game rebuilt these colours: either the buffer changed shape
            // (a tree switching growth state does this) or its contents no longer match what
            // was written last time, which only happens when MeshColorSystem has run.
            bool recapture = originals.Length != meshColors.Length
                          || !Approximately(meshColors[0].m_ColorSet.m_Channel0, charred.m_LastWritten);

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

            float amount = math.saturate(charred.m_Amount) * strength;

            for (int i = 0; i < meshColors.Length; i++)
            {
                MeshColor color = meshColors[i];
                color.m_ColorSet = CharringSystem.Char(originals[i].ColorSet, amount);
                meshColors[i] = color;
            }

            charred.m_LastWritten = meshColors[0].m_ColorSet.m_Channel0;
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
