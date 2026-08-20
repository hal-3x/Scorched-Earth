using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Unity.Mathematics;

namespace ScorchedEarth
{
    /// <summary>
    /// User-facing options. Defaults are tuned to look like the screenshots the mod was
    /// designed against; every performance-relevant number is exposed so the mod can be
    /// scaled down on weaker machines without editing code.
    /// </summary>
    [FileLocation("ModsSettings/ScorchedEarth/ScorchedEarth")]
    [SettingsUITabOrder(kVisualsTab, kPerformanceTab, kAboutTab)]
    [SettingsUIGroupOrder(kCharringGroup, kBudgetGroup, kAboutGroup)]
    [SettingsUIShowGroupName(kCharringGroup, kBudgetGroup)]
    public class ScorchedEarthSettings : ModSetting
    {
        public const string kVisualsTab = "Visuals";
        public const string kPerformanceTab = "Performance";
        public const string kAboutTab = "About";

        public const string kCharringGroup = "Charring and recovery";
        public const string kBudgetGroup = "Update rate";
        public const string kAboutGroup = "About";

        public ScorchedEarthSettings(IMod mod) : base(mod)
        {
        }

        // ---------------------------------------------------------------- charring

        [SettingsUISection(kVisualsTab, kCharringGroup)]
        public bool CharBuildings { get; set; }

        [SettingsUISection(kVisualsTab, kCharringGroup)]
        public bool CharTrees { get; set; }

        /// <summary>How black a fully charred surface goes. 0 = untouched, 1 = soot black.</summary>
        [SettingsUISlider(min = 10f, max = 95f, step = 5f, unit = "percentage")]
        [SettingsUISection(kVisualsTab, kCharringGroup)]
        public int CharStrength { get; set; }

        /// <summary>In-game days for a charred surface to return to its original colour.</summary>
        [SettingsUISlider(min = 1f, max = 60f, step = 1f, unit = "integer")]
        [SettingsUISection(kVisualsTab, kCharringGroup)]
        public int CharRecoveryDays { get; set; }

        /// <summary>In-game days for a fire-killed tree to come back to life.</summary>
        [SettingsUISlider(min = 1f, max = 120f, step = 1f, unit = "integer")]
        [SettingsUISection(kVisualsTab, kCharringGroup)]
        public int TreeRecoveryDays { get; set; }

        /// <summary>Simulation frames between charring updates. Higher = cheaper, less responsive.</summary>
        [SettingsUISlider(min = 4f, max = 128f, step = 4f, unit = "integer")]
        [SettingsUISection(kPerformanceTab, kBudgetGroup)]
        public int UpdateInterval { get; set; }

        // ------------------------------------------------------------------- about

        [SettingsUISection(kAboutTab, kAboutGroup)]
        public string ModVersion => Mod.Version;

        /// <summary>Extra logging for diagnosing effect discovery. Off by default.</summary>
        [SettingsUISection(kAboutTab, kAboutGroup)]
        public bool VerboseLogging { get; set; }

        public override void SetDefaults()
        {
            CharBuildings = true;
            CharTrees = true;
            CharStrength = 65;
            CharRecoveryDays = 14;
            TreeRecoveryDays = 45;

            UpdateInterval = 16;

            VerboseLogging = false;
        }

        // Convenience accessors in the units the systems actually work in.
        //
        // Every one of these clamps. Settings arrive from a file on disk that can predate a
        // version of this mod, be hand-edited, or fail to load at all - and a zero here means
        // a division by zero or a zero-size budget rather than a merely odd-looking city.

        public float CharStrengthNormalized => math.saturate(CharStrength / 100f);

        /// <summary>Recovery rate per simulation frame for charring.</summary>
        public float CharRecoveryPerFrame => 1f / (math.max(1, CharRecoveryDays) * Mod.kFramesPerDay);

        /// <summary>Recovery rate per simulation frame for fire-killed trees.</summary>
        public float TreeRecoveryPerFrame => 1f / (math.max(1, TreeRecoveryDays) * Mod.kFramesPerDay);

        /// <summary>Frames between visual rebuilds, never zero.</summary>
        public int SafeUpdateInterval => math.max(1, UpdateInterval);

    }
}
