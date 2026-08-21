using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Prefabs;
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
    [SettingsUITabOrder(kSimulationTab, kVisualsTab, kPerformanceTab, kAboutTab)]
    [SettingsUIGroupOrder(kSimulationGroup, kToolGroup, kCharringGroup, kGroundGroup, kBudgetGroup, kAboutGroup)]
    [SettingsUIShowGroupName(kSimulationGroup, kToolGroup, kCharringGroup, kGroundGroup, kBudgetGroup)]
    public class ScorchedEarthSettings : ModSetting
    {
        public const string kSimulationTab = "Fire";
        public const string kVisualsTab = "Visuals";
        public const string kPerformanceTab = "Performance";
        public const string kAboutTab = "About";

        public const string kCharringGroup = "Charring and recovery";
        public const string kGroundGroup = "Scorched ground";
        public const string kSimulationGroup = "Fire simulation";
        public const string kToolGroup = "Fire tool";
        public const string kBudgetGroup = "Update rate";
        public const string kAboutGroup = "About";

        public ScorchedEarthSettings(IMod mod) : base(mod)
        {
        }

        // -------------------------------------------------------- fire simulation

        // Percentages rather than presets, and split by what is burning. Five named tiers
        // could not express "a bit less than that", and one tier had to serve fire crossing a
        // firebreak and fire crossing a garden fence at once - which is why balancing them
        // against each other kept failing. 100 is the game as shipped, in every case.

        /// <summary>How readily fire moves from one building to another, 100 = vanilla.</summary>
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage")]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public int BuildingSpread { get; set; }

        /// <summary>
        /// How far a burning building can reach, 100 = vanilla.
        ///
        /// <para>Separate from the chance above because it is a different question: reach
        /// decides whether the next building is a candidate at all, and the chance only
        /// matters once it is. Below the reach, no amount of the other setting does
        /// anything.</para>
        /// </summary>
        [SettingsUISlider(min = 25f, max = 300f, step = 5f, unit = "percentage")]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public int BuildingSpreadRange { get; set; }

        /// <summary>How readily fire moves through trees and vegetation, 100 = vanilla.</summary>
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage")]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public int VegetationSpread { get; set; }

        /// <summary>How far a burning tree can reach, 100 = vanilla.</summary>
        [SettingsUISlider(min = 25f, max = 300f, step = 5f, unit = "percentage")]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public int VegetationSpreadRange { get; set; }

        /// <summary>How quickly a burning building falls down, 100 = vanilla.</summary>
        [SettingsUISlider(min = 25f, max = 400f, step = 5f, unit = "percentage")]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public int CollapseSpeed { get; set; }

        /// <summary>Puts all three sliders back to the values the game ships with.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public bool ResetFireTuning
        {
            set
            {
                BuildingSpread = 100;
                BuildingSpreadRange = 100;
                VegetationSpread = 100;
                VegetationSpreadRange = 100;
                CollapseSpeed = 100;
                ApplyAndSave();
            }
        }

        /// <summary>
        /// Standing note for the fire tab. Both settings above rewrite prefab data, which is
        /// rebuilt from the game's assets on every launch and never written to a save.
        /// </summary>
        [SettingsUIMultilineText]
        [SettingsUISection(kSimulationTab, kSimulationGroup)]
        public string FireSimulationNote => string.Empty;

        // -------------------------------------------------------------- fire tool

        /// <summary>Arms the ignite tool; the next click in the world starts a fire.</summary>
        [SettingsUIButton]
        [SettingsUISection(kSimulationTab, kToolGroup)]
        public bool ArmIgniteTool
        {
            set
            {
                Systems.IgniteToolSystem tool = Mod.IgniteTool;
                if (tool == null)
                {
                    Mod.log.Warn("Ignite tool is not available - load a city first.");
                    return;
                }

                tool.Arm();
            }
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

        // ---------------------------------------------------------- scorched ground

        /// <summary>Paint burnt ground under fires, using the game's surface painter.</summary>
        [SettingsUISection(kVisualsTab, kGroundGroup)]
        public bool ScorchGround { get; set; }

        /// <summary>
        /// Which paintable surface channel to burn into, Extra1 through Extra4.
        ///
        /// <para>The user splatmap is a two-channel <c>R8G8</c> texture, which looks at first
        /// like a limit of two materials - but it is an input to the splat material, which
        /// blits into the four-channel map bound as <c>colossal_Splatmap</c>. Two channels
        /// selecting between grass, dirt, rock and four extras is an index and a weight, not
        /// one channel per material, so all four extras are reachable.</para>
        ///
        /// <para>They are shared map-wide, and what each one looks like is decided by the
        /// map's terrain render settings rather than by this mod - so which to use has to be
        /// the player's choice. Pick the one your map dresses as dirt or burnt ground.</para>
        /// </summary>
        [SettingsUISlider(min = 1f, max = 4f, step = 1f, unit = "integer")]
        [SettingsUISection(kVisualsTab, kGroundGroup)]
        public int ScorchChannel { get; set; }

        /// <summary>Radius in metres of the scorch left under a fire.</summary>
        [SettingsUISlider(min = 16f, max = 320f, step = 16f, unit = "integer")]
        [SettingsUISection(kVisualsTab, kGroundGroup)]
        public int ScorchRadius { get; set; }

        /// <summary>How strongly each stroke stains the ground.</summary>
        [SettingsUISlider(min = 5f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(kVisualsTab, kGroundGroup)]
        public int ScorchOpacity { get; set; }

        /// <summary>
        /// Standing warning shown in the scorched-ground group. Burn scars cannot be undone
        /// by this mod - erasing a splatmap channel clears every channel at once, which would
        /// destroy the player's own painting - so the disclaimer sits next to the switch that
        /// turns it on rather than in a readme nobody opens.
        /// </summary>
        [SettingsUIMultilineText]
        [SettingsUISection(kVisualsTab, kGroundGroup)]
        public string ScorchWarning => string.Empty;

        /// <summary>Simulation frames between charring updates. Higher = cheaper, less responsive.</summary>
        [SettingsUISlider(min = 4f, max = 128f, step = 4f, unit = "integer")]
        [SettingsUISection(kPerformanceTab, kBudgetGroup)]
        public int UpdateInterval { get; set; }

        // ------------------------------------------------------------------- about

        [SettingsUISection(kAboutTab, kAboutGroup)]
        public string ModVersion => Mod.Version;

        /// <summary>Extra logging for diagnosing charring decisions. Off by default.</summary>
        [SettingsUISection(kAboutTab, kAboutGroup)]
        public bool VerboseLogging { get; set; }

        public override void SetDefaults()
        {
            CharBuildings = true;
            CharTrees = true;
            CharStrength = 65;
            CharRecoveryDays = 14;
            TreeRecoveryDays = 45;

            // Off by default, unlike everything else here. Scorching writes into a shared
            // splatmap channel whose appearance the map decides, and the marks are permanent
            // - so which channel to use, and whether to use one at all, is a choice the
            // player has to make with their own map in front of them.
            BuildingSpread = 100;
            BuildingSpreadRange = 100;
            VegetationSpread = 100;
            VegetationSpreadRange = 100;
            CollapseSpeed = 100;

            ScorchGround = false;
            ScorchChannel = 1;
            // The playable-area splatmap is 4096 texels across roughly 14 km and samples with
            // point filtering, so a small brush lands as a handful of blocky texels.
            ScorchRadius = 128;
            ScorchOpacity = 60;

            UpdateInterval = 16;

            VerboseLogging = false;
        }

        // Convenience accessors in the units the systems actually work in.
        //
        // Every one of these carries [SettingsUIHidden]. The options screen reflects over
        // public properties and builds a control for each, so without it they appear as
        // editable rows - and one returning an enum came out as a dropdown in a nameless
        // fourth tab, because it had no [SettingsUISection] to file it under.
        //
        // Every one of these clamps. Settings arrive from a file on disk that can predate a
        // version of this mod, be hand-edited, or fail to load at all - and a zero here means
        // a division by zero or a zero-size budget rather than a merely odd-looking city.

        [SettingsUIHidden]
        public float CharStrengthNormalized => math.saturate(CharStrength / 100f);

        /// <summary>Recovery rate per simulation frame for charring.</summary>
        [SettingsUIHidden]
        public float CharRecoveryPerFrame => 1f / (math.max(1, CharRecoveryDays) * Mod.kFramesPerDay);

        /// <summary>Recovery rate per simulation frame for fire-killed trees.</summary>
        [SettingsUIHidden]
        public float TreeRecoveryPerFrame => 1f / (math.max(1, TreeRecoveryDays) * Mod.kFramesPerDay);

        /// <summary>Frames between visual rebuilds, never zero.</summary>
        [SettingsUIHidden]
        public int SafeUpdateInterval => math.max(1, UpdateInterval);

        [SettingsUIHidden]
        public float ScorchOpacityNormalized => math.saturate(ScorchOpacity / 100f);

        // ------------------------------------------------- fire tuning, in usable units
        //
        // Each slider is a plain multiplier on how often the thing it names happens, with 1
        // meaning vanilla. Turning that into the numbers the simulation wants is the fiddly
        // part, and it is done here so the tuning system reads as intent rather than algebra.

        [SettingsUIHidden]
        public float BuildingSpreadFactor => math.max(0.01f, BuildingSpread / 100f);

        [SettingsUIHidden]
        public float VegetationSpreadFactor => math.max(0.01f, VegetationSpread / 100f);

        [SettingsUIHidden]
        public float BuildingRangeFactor => math.max(0.01f, BuildingSpreadRange / 100f);

        [SettingsUIHidden]
        public float VegetationRangeFactor => math.max(0.01f, VegetationSpreadRange / 100f);

        [SettingsUIHidden]
        public float CollapseSpeedFactor => math.max(0.01f, CollapseSpeed / 100f);

        /// <summary>
        /// What to multiply <c>FireData.m_SpreadProbability</c> by for a given slider.
        ///
        /// <para>The simulation takes <c>sqrt(m_SpreadProbability * 0.01)</c>, so the stored
        /// number has to be squared to move the effective one linearly. Squaring here is what
        /// makes the slider feel proportional instead of flattening out as it is dragged.
        /// </para>
        /// </summary>
        public static float SpreadProbabilityScale(float factor)
        {
            return factor * factor;
        }

        /// <summary>The chosen channel as the enum the painter actually takes.</summary>
        [SettingsUIHidden]
        public TerrainMaterialType ScorchMaterialType
        {
            get
            {
                switch (math.clamp(ScorchChannel, 1, 4))
                {
                    case 1: return TerrainMaterialType.Extra1;
                    case 2: return TerrainMaterialType.Extra2;
                    case 3: return TerrainMaterialType.Extra3;
                    default: return TerrainMaterialType.Extra4;
                }
            }
        }

    }
}
