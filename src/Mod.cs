using System;
using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.Rendering;
using Game.SceneFlow;
using Unity.Entities;
using ScorchedEarth.Systems;

namespace ScorchedEarth
{
    /// <summary>
    /// Entry point. Registers the mod's systems and settings and nothing else.
    ///
    /// Design note on privileges: this mod does not use Harmony and patches no game code,
    /// creates no entities, and edits no prefabs. It reads the fire simulation's own damage
    /// figures and writes back mesh colours and tree state - nothing more. It cannot
    /// desynchronise the simulation and switches off cleanly.
    /// </summary>
    public sealed class Mod : IMod
    {
        public const string Name = "Scorched Earth";
        public const string Version = "1.0.0";

        /// <summary>Simulation frames in one in-game day (matches Game.Simulation.TimeSystem).</summary>
        public const float kFramesPerDay = 262144f;

        public static readonly ILog log =
            LogManager.GetLogger(nameof(ScorchedEarth)).SetShowsErrorsInUI(false);

        public static Mod Instance { get; private set; }

        public ScorchedEarthSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Instance = this;
            log.Info($"{Name} {Version} loading.");

            // ModSetting's constructor does not apply defaults, so both the live instance and
            // the fallback handed to LoadSettings have to be primed explicitly. Without this
            // a fresh install comes up with every slider at zero and every toggle off - and a
            // zero update interval is not a sane starting point.
            Settings = new ScorchedEarthSettings(this);
            Settings.SetDefaults();

            ScorchedEarthSettings defaults = new ScorchedEarthSettings(this);
            defaults.SetDefaults();

            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(nameof(ScorchedEarth), Settings, defaults);

            // Simulation-time work: how charred things are, and recovering from it.
            updateSystem.UpdateAt<CharringSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RecoverySystem>(SystemUpdatePhase.GameSimulation);

            // Ground scorching paints into the terrain splatmap through the game's own
            // surface painter. It creates no entities and builds no geometry.
            updateSystem.UpdateAt<ScorchSurfaceSystem>(SystemUpdatePhase.GameSimulation);

            // Retunes the fire simulation by rewriting the prefab data it reads.
            updateSystem.UpdateAt<FireTuningSystem>(SystemUpdatePhase.GameSimulation);

            // The ignite tool runs in the tool phases, like every other tool.
            updateSystem.UpdateAt<IgniteToolSystem>(SystemUpdatePhase.ToolUpdate);
            IgniteTool = World.DefaultGameObjectInjectionWorld
                              .GetOrCreateSystemManaged<IgniteToolSystem>();

            // Charred colours are written straight after the game rebuilds mesh colours, and
            // before the renderer uploads them. UpdateAfter pins the ordering explicitly
            // rather than relying on registration order within the phase.
            updateSystem.UpdateAfter<CharColorSystem, MeshColorSystem>(SystemUpdatePhase.PreCulling);

            log.Info($"{Name} systems registered.");
        }

        public void OnDispose()
        {
            log.Info($"{Name} unloading.");

            Settings?.UnregisterInOptionsUI();
            Settings = null;
            IgniteTool = null;
            Instance = null;
        }

        /// <summary>
        /// The ignite tool, so the options screen's button can reach it. Null until a city is
        /// loaded, which is why the button checks before using it.
        /// </summary>
        public static Systems.IgniteToolSystem IgniteTool { get; private set; }

        /// <summary>Settings accessor that is safe to call before/after load.</summary>
        public static ScorchedEarthSettings ActiveSettings => Instance?.Settings;

        /// <summary>
        /// Whether verbose logging is on. Test this before building a message: passing a
        /// lambda to <see cref="Verbose"/> allocates a closure at the call site whether or
        /// not logging is enabled, which matters in loops that run once per burned tree.
        /// </summary>
        public static bool IsVerbose
        {
            get
            {
                var settings = ActiveSettings;
                return settings != null && settings.VerboseLogging;
            }
        }

        /// <summary>Verbose log helper - avoids building strings when verbose is off.</summary>
        public static void Verbose(Func<string> message)
        {
            if (IsVerbose)
            {
                log.Info(message());
            }
        }
    }
}
