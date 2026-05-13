using System;
using System.Linq;
using UnityEngine;

namespace MuMech
{
    public class MechJebModuleScienceWindow : DisplayModule
    {
        [Persistent(pass = (int)Pass.GLOBAL)]
        public int recommendationCount = 5;

        private ScienceGuidance.TotalsDto _totals;
        private ScienceGuidance.HereDto _here;
        private ScienceGuidance.NextEasyDto _next;
        private string _error;
        private double _lastRefreshUt = -1;

        public MechJebModuleScienceWindow(MechJebCore core) : base(core)
        {
            ShowInEditor = false;
            ShowInFlight = true;
        }

        public override void OnStart(PartModule.StartState state)
        {
            Refresh();
            base.OnStart(state);
        }

        protected override GUILayoutOption[] WindowOptions() => new[] { GuiUtils.LayoutWidth(360), GUILayout.Height(30) };

        protected override void WindowGUI(int windowID)
        {
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GuiUtils.LayoutWidth(80))) Refresh();
            GUILayout.Label("Top", GuiUtils.LayoutNoExpandWidth);
            string countText = GUILayout.TextField(recommendationCount.ToString(), GuiUtils.LayoutWidth(32));
            if (int.TryParse(countText, out int parsed))
            {
                recommendationCount = Math.Max(1, Math.Min(10, parsed));
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            if (_totals == null || _here == null || _next == null) Refresh();

            if (!string.IsNullOrEmpty(_error))
            {
                GUILayout.Label(_error, GuiUtils.YellowLabel);
                GUILayout.EndVertical();
                base.WindowGUI(windowID);
                return;
            }

            if (_totals != null && string.Equals(_totals.mode, "sandbox", StringComparison.Ordinal))
            {
                GUILayout.Label("Science mode is disabled in sandbox saves.");
                GUILayout.EndVertical();
                base.WindowGUI(windowID);
                return;
            }

            if (_here != null)
            {
                GUILayout.Label("Here");
                Row("Body", _here.body);
                Row("Situation", _here.situation);
                Row("Biome", string.IsNullOrEmpty(_here.biome) ? "-" : _here.biome);
            }

            if (_totals != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("Totals");
                Row("Earned", FormatScience(_totals.total_earned));
                Row("Subjects", _totals.subjects_touched.ToString());
            }

            if (_here != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("Available Here");
                int shown = 0;
                foreach (ScienceGuidance.HereExperimentDto experiment in _here.experiments
                             .Where(e => e.valid_here && e.onboard && e.science_remaining_here > 0.01f)
                             .Take(5))
                {
                    Row(experiment.title, FormatScience(experiment.science_remaining_here));
                    shown++;
                }
                if (shown == 0) GUILayout.Label("No onboard science remains at this location.");
            }

            if (_next != null)
            {
                GUILayout.Space(4);
                GUILayout.Label("Next Easy");
                int shown = 0;
                foreach (ScienceGuidance.RecommendationDto recommendation in _next.recommendations.Take(recommendationCount))
                {
                    GUILayout.Label(recommendation.title + " - " + FormatScience(recommendation.science_remaining));
                    GUILayout.Label(recommendation.tier + ": " + recommendation.situation + BiomeSuffix(recommendation.biome));
                    shown++;
                }
                if (shown == 0) GUILayout.Label("No easy onboard recommendations found.");
            }

            if (_lastRefreshUt >= 0) GUILayout.Label("Updated UT " + _lastRefreshUt.ToString("F0"));

            GUILayout.EndVertical();
            base.WindowGUI(windowID);
        }

        private void Refresh()
        {
            _error = "";
            try
            {
                _totals = ScienceGuidance.Totals();
                _here = ScienceGuidance.Here(Vessel);
                _next = ScienceGuidance.NextEasy(Vessel, recommendationCount);
                _lastRefreshUt = Planetarium.GetUniversalTime();
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
        }

        private static void Row(string left, string right)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(left, GuiUtils.LayoutExpandWidth);
            GUILayout.Label(right, GuiUtils.LayoutNoExpandWidth);
            GUILayout.EndHorizontal();
        }

        private static string FormatScience(float value) => value.ToString("F1") + " science";

        private static string BiomeSuffix(string biome) => string.IsNullOrEmpty(biome) ? "" : " / " + biome;

        public override string GetName() => "Science";

        public override string IconName() => "Science";
    }
}
