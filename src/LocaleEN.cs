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
                    "How quickly fire will spread between buildings." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.BuildingSpreadRange)), "Building reach" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.BuildingSpreadRange)),
                    "How far the fire from one burning building will spread to another burning building." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.VegetationSpread)), "Tree to tree" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.VegetationSpread)),
                    "How quickly fire will spread throught vegetation." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.VegetationSpreadRange)), "Tree reach" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.VegetationSpreadRange)),
                    "How far the fire from burning tree will spread to another burning tree." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CollapseSpeed)), "Collapse speed" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CollapseSpeed)),
                    "How quickly a building will collapse from fire damage. Higher values make buildings collapse sooner and lower values make it take longer." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ResetFireTuning)), "Reset to default" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ResetFireTuning)),
                    "Restores vanilla fire behavior by setting every slider back to 100%" },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.FireSimulationNote)), "" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.FireSimulationNote)),
                    "Both settings work by rewriting the values the game's own fire simulation reads." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ArmIgniteTool)), "Start a fire" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ArmIgniteTool)),
                    "Debug tool to spawn fires on vegetation or objects. Click this button and close out of settings, buildings will highlight orange when hovered over and trees will not (known bug), click to start a fire. Right click to cancel." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharBuildings)), "Char buildings" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharBuildings)),
                    "Buildings that survive a fire are darkened by soot, then slowly clean up over time." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharTrees)), "Char and kill trees" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharTrees)),
                    "Burned trees switch to the bare dead-tree model, darkened to look charred, and regrow later. Trees may flicker from their dead model to their living model from time to time, I have no idea why, just try to ignore it." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharStrength)), "Char strength" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharStrength)),
                    "How dark a fully charred surface becomes. Higher values look more burnt." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.CharRecoveryDays)), "Char recovery (days)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.CharRecoveryDays)),
                    "In-game days for a charred surface to return to its original colour. Unsure if this works, largely untested. Burned areas may need to be cleared to remove char effects." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.TreeRecoveryDays)), "Tree recovery (days)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.TreeRecoveryDays)),
                    "In-game days for a fire-killed tree to come back to life. Unsure if this works, largely untested. Burned areas may need to be cleared to remove char effects." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchGround)), "Scorch the ground" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchGround)),
                    "WARNING, BURN MARKS ARE PERMANENT. This setting will paint surfaces on the ground beneath fire sources. What is painted will not be automatically removed, you will have to repaint it yourself and will damage your work!! Use Cautiously!!" },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchChannel)), "Surface channel" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchChannel)),
                    "Which of the four paintable surface channels to paint scorch marks." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchRadius)), "Scorch radius (m)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchRadius)),
                    "How wide a patch of burnt ground each fire leaves behind." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchOpacity)), "Scorch strength" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchOpacity)),
                    "How strongly/opacity each fire paints the ground beneath it." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.ScorchWarning)), "" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.ScorchWarning)),
                    "Burn scars are permanent. To remove a scar, paint over it yourself with the landscaping surface tool." },

                { m_Setting.GetOptionLabelLocaleID(nameof(ScorchedEarthSettings.UpdateInterval)), "Update interval (frames)" },
                { m_Setting.GetOptionDescLocaleID(nameof(ScorchedEarthSettings.UpdateInterval)),
                    "Simulation frames between charring updates. Higher values cost performance, lower values are less accurate." },

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
