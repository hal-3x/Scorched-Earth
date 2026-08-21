using System.Collections.Generic;
using Colossal;

namespace ScorchedEarth
{
    /// <summary>English strings for the options screen.</summary>
    public sealed class LocaleEN : IDictionarySource
    {
        private readonly ScorchedEarthSettings m_Setting;

        public LocaleEN(ScorchedEarthSettings setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), Mod.Name },

                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kSimulationTab), "Fire" },
                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kVisualsTab), "Visuals" },
                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kPerformanceTab), "Performance" },
                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kAboutTab), "About" },

                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kSimulationGroup), "Spread and ignition" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kToolGroup), "Fire tool" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kCharringGroup), "Charring and recovery" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kGroundGroup), "Scorched ground" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kBudgetGroup), "Update rate" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kAboutGroup), "About" },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.BuildingSpread)), "Building to building" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.BuildingSpread)),
                    "How readily fire moves from one building to the next, where 100% is the game as shipped. This covers both how far a burning building can reach and how easily its neighbours catch. Spontaneous fires are held at the vanilla rate whatever you set here." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.BuildingSpreadRange)), "Building reach" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.BuildingSpreadRange)),
                    "How far a burning building can reach, where 100% is the game as shipped. This is the gate: a neighbour further away than this is never even considered, so the chance above does nothing until the reach covers it. Raising it costs performance during large fires, because the game searches this radius around every burning object." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.VegetationSpread)), "Tree to tree" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.VegetationSpread)),
                    "How readily fire moves through trees and vegetation, where 100% is the game as shipped. Separate from buildings because a forest fire crossing a firebreak and a house fire crossing a garden fence want very different numbers." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.VegetationSpreadRange)), "Tree reach" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.VegetationSpreadRange)),
                    "How far a burning tree can reach, where 100% is the game as shipped. Raise it to let fire jump firebreaks and gaps in woodland; lower it to make clearings stop a fire. Costs performance during large fires for the same reason as building reach." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CollapseSpeed)), "Collapse speed" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CollapseSpeed)),
                    "How quickly a burning building falls down, where 100% is the game as shipped. Note that this works against spread: a building that collapses quickly stops being a fire source, so a high setting gives you fewer fires that travel, not more." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ResetFireTuning)), "Reset to default" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ResetFireTuning)),
                    "Puts every slider on this tab back to 100%, which is the fire simulation exactly as the game ships it." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.FireSimulationNote)), "" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.FireSimulationNote)),
                    "Both settings work by rewriting the values the game's own fire simulation reads. Nothing is written to your save - the game rebuilds those values from its assets every launch - so setting them back to Vanilla, or removing this mod, restores shipped behaviour exactly." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ArmIgniteTool)), "Start a fire" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ArmIgniteTool)),
                    "Arms the fire tool. Close this menu and click a building or tree to set it alight; right-click cancels. The fire it starts is a real one - it spreads, damages and calls out fire engines like any other." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharBuildings)), "Char buildings" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharBuildings)),
                    "Buildings that survive a fire are darkened by soot, then slowly clean up over time." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharTrees)), "Char and kill trees" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharTrees)),
                    "Burned trees switch to the bare dead-tree model, darkened to look charred, and regrow later." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharStrength)), "Char strength" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharStrength)),
                    "How dark a fully charred surface becomes. Higher values look more burnt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharRecoveryDays)), "Char recovery (days)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharRecoveryDays)),
                    "In-game days for a charred surface to return to its original colour." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.TreeRecoveryDays)), "Tree recovery (days)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.TreeRecoveryDays)),
                    "In-game days for a fire-killed tree to come back to life." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchGround)), "Scorch the ground" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchGround)),
                    "Paint burnt ground under fires with the game's surface painter. Burn scars are permanent - paint over them yourself if you want them gone." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchChannel)), "Surface channel" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchChannel)),
                    "Which of the four paintable surface channels to burn into. What each one looks like is set by your map, not by this mod - so pick the one your map dresses as dirt or burnt ground, and one you are not already using for something else." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchRadius)), "Scorch radius (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchRadius)),
                    "How wide a patch of burnt ground each fire leaves behind." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchOpacity)), "Scorch strength" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchOpacity)),
                    "How strongly each fire stains the ground beneath it." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchWarning)), "" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchWarning)),
                    "Burn scars are permanent. This mod paints them and never erases them - clearing a surface channel wipes every channel at once, which would destroy your own painted surfaces wherever a fire reached. To remove a scar, paint over it yourself with the landscaping surface tool." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.UpdateInterval)), "Update interval (frames)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.UpdateInterval)),
                    "Simulation frames between charring updates. Higher is cheaper but slower to react." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ModVersion)), "Version" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ModVersion)), "Installed mod version." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.VerboseLogging)), "Verbose logging" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.VerboseLogging)),
                    "Log charring and tree-death decisions to the player log. Useful when reporting a problem." },
            };
        }

        public void Unload()
        {
        }
    }
}
