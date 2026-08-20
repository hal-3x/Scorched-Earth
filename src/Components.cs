using Colossal.Serialization.Entities;
using Game.Rendering;
using Unity.Entities;
using Color = UnityEngine.Color;

namespace ScorchedEarth
{
    /// <summary>
    /// Attached to any object that has been through a fire and survived it. Drives the
    /// charred tint and its slow fade back to the original colours.
    ///
    /// <para>Serialized, so charring survives a save/load. The type name is part of the
    /// save format: renaming this struct invalidates charring in existing saves (the rest
    /// of the save is unaffected, and the objects simply come back clean).</para>
    /// </summary>
    public struct Charred : IComponentData, IQueryTypeParameter, ISerializable
    {
        /// <summary>Current char level, 0 (clean) to 1 (fully blackened).</summary>
        public float m_Amount;

        /// <summary>Highest char level reached. Decides whether a tree dies outright.</summary>
        public float m_Peak;

        /// <summary>
        /// Char level the mesh colours were last written at. Recolouring dirties a render
        /// batch, so colours are only rewritten once the value drifts far enough to be
        /// visible - see <see cref="Systems.CharringSystem"/>.
        /// </summary>
        public float m_AppliedAmount;

        /// <summary>
        /// The first colour channel this mod last wrote into the object's mesh colours.
        ///
        /// <para>It is how the mod notices that the game recomputed an object's colours
        /// underneath it. <c>MeshColorSystem</c> rebuilds mesh colours from the prefab
        /// whenever an object is marked dirty - for a season change, a level-up, or a tree
        /// changing growth state. When what is in the buffer no longer matches what was
        /// written, those colours are known-clean and are re-captured as the new base.
        /// Without that check the mod would darken its own output and objects would fade to
        /// black over time.</para>
        /// </summary>
        public Color m_LastWritten;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Amount);
            writer.Write(m_Peak);
            writer.Write(m_AppliedAmount);
            writer.Write(m_LastWritten);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Amount);
            reader.Read(out m_Peak);
            reader.Read(out m_AppliedAmount);
            reader.Read(out m_LastWritten);
        }
    }

    /// <summary>
    /// The object's mesh colours as the game computed them, before the mod darkened
    /// anything. One entry per entry of the object's <c>MeshColor</c> buffer.
    ///
    /// <para>Charred colours are always derived from these rather than from whatever is
    /// currently on screen, so darkening is idempotent - it never compounds frame to frame -
    /// and clearing the char lands exactly back on the original look.</para>
    /// </summary>
    [InternalBufferCapacity(1)]
    public struct OriginalMeshColor : IBufferElementData, ISerializable
    {
        public Color m_Channel0;
        public Color m_Channel1;
        public Color m_Channel2;

        public ColorSet ColorSet
        {
            get
            {
                ColorSet set = default(ColorSet);
                set.m_Channel0 = m_Channel0;
                set.m_Channel1 = m_Channel1;
                set.m_Channel2 = m_Channel2;
                return set;
            }
            set
            {
                m_Channel0 = value.m_Channel0;
                m_Channel1 = value.m_Channel1;
                m_Channel2 = value.m_Channel2;
            }
        }

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Channel0);
            writer.Write(m_Channel1);
            writer.Write(m_Channel2);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Channel0);
            reader.Read(out m_Channel1);
            reader.Read(out m_Channel2);
        }
    }

    /// <summary>
    /// Attached to a tree that fire killed. The tree is switched to the vanilla dead
    /// state so it renders with the bare dead-tree mesh, then slowly regrows.
    /// </summary>
    public struct FireKilledTree : IComponentData, IQueryTypeParameter, ISerializable
    {
        /// <summary>Regrowth progress, 0 (just died) to 1 (returned to life).</summary>
        public float m_Regrowth;

        /// <summary>Growth byte captured before the fire, restored on recovery.</summary>
        public byte m_OriginalGrowth;

        /// <summary>Tree state captured before the fire, restored on recovery.</summary>
        public byte m_OriginalState;

        public void Serialize<TWriter>(TWriter writer) where TWriter : IWriter
        {
            writer.Write(m_Regrowth);
            writer.Write(m_OriginalGrowth);
            writer.Write(m_OriginalState);
        }

        public void Deserialize<TReader>(TReader reader) where TReader : IReader
        {
            reader.Read(out m_Regrowth);
            reader.Read(out m_OriginalGrowth);
            reader.Read(out m_OriginalState);
        }
    }

}
