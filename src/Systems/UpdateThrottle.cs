namespace ScorchedEarth.Systems
{
    /// <summary>
    /// Rate-limits a system to a user-configurable number of simulation frames.
    ///
    /// <para>The game's own <c>GetUpdateInterval</c> cannot do this job. It must return a
    /// power of two - the scheduler throws otherwise - and it is read once when the system
    /// is registered, so a value taken from a setting would neither be legal for every
    /// slider position nor react when the player changed it. Each system therefore declares
    /// the fastest rate it will ever need as a power of two, and throttles down to the
    /// user's interval here.</para>
    ///
    /// <para>Callers also get the number of frames that actually elapsed, so rates can be
    /// expressed per in-game day and stay correct no matter how the interval is configured
    /// or how the simulation speed changes.</para>
    /// </summary>
    public struct UpdateThrottle
    {
        private uint m_LastFrame;
        private bool m_Started;

        /// <summary>
        /// Decides whether the caller should do its work this tick.
        /// </summary>
        /// <param name="frameIndex">Current simulation frame.</param>
        /// <param name="interval">Minimum frames between runs.</param>
        /// <param name="elapsed">Frames since the previous run. Valid only when this returns true.</param>
        public bool ShouldRun(uint frameIndex, int interval, out uint elapsed)
        {
            if (!m_Started)
            {
                // First run: charge one interval so rates start from a sensible step rather
                // than from however long this save has been running.
                m_Started = true;
                m_LastFrame = frameIndex;
                elapsed = (uint)(interval > 0 ? interval : 1);
                return true;
            }

            if (frameIndex < m_LastFrame)
            {
                // The frame counter went backwards - a different save was loaded. Restart
                // rather than reporting an enormous elapsed time.
                m_LastFrame = frameIndex;
                elapsed = (uint)(interval > 0 ? interval : 1);
                return true;
            }

            uint since = frameIndex - m_LastFrame;
            if (since < (uint)(interval > 0 ? interval : 1))
            {
                elapsed = 0u;
                return false;
            }

            m_LastFrame = frameIndex;
            elapsed = since;
            return true;
        }

        /// <summary>Forgets the last run, so the next call is treated as a first run.</summary>
        public void Reset()
        {
            m_Started = false;
            m_LastFrame = 0u;
        }
    }
}
