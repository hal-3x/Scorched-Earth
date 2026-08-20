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

                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kVisualsTab), "Visuals" },
                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kPerformanceTab), "Performance" },
                { m_Setting.GetOptionTabLocaleID(ScorchedEarthSettings.kAboutTab), "About" },

                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kCharringGroup), "Charring and recovery" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kGroundGroup), "Scorched ground" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kBudgetGroup), "Update rate" },
                { m_Setting.GetOptionGroupLocaleID(ScorchedEarthSettings.kAboutGroup), "About" },

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
