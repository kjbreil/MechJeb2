using System;
using System.Collections.Generic;
using System.Linq;

namespace MuMech
{
    public static class ScienceGuidance
    {
        private static readonly ExperimentSituations[] AllSituations =
        {
            ExperimentSituations.SrfLanded,
            ExperimentSituations.SrfSplashed,
            ExperimentSituations.FlyingLow,
            ExperimentSituations.FlyingHigh,
            ExperimentSituations.InSpaceLow,
            ExperimentSituations.InSpaceHigh,
        };

        public sealed class TotalsDto
        {
            public string mode;
            public float total_earned;
            public int subjects_touched;
            public List<BodyTotalsDto> per_body = new List<BodyTotalsDto>();
        }

        public sealed class BodyTotalsDto
        {
            public string body;
            public float earned;
            public float earned_cap_for_touched;
            public int subjects_touched;
        }

        public sealed class EarnedDto
        {
            public string mode;
            public int total_subjects;
            public int returned;
            public bool truncated;
            public List<SubjectDto> subjects = new List<SubjectDto>();
        }

        public sealed class SubjectDto
        {
            public string subject_id;
            public string title;
            public string body;
            public string situation;
            public string biome;
            public string experiment_id;
            public float science_earned;
            public float science_cap;
            public float science_remaining;
            public float percent_complete;
        }

        public sealed class HereDto
        {
            public string mode;
            public string body;
            public string situation;
            public string biome;
            public bool biome_relevant;
            public List<HereExperimentDto> experiments = new List<HereExperimentDto>();
        }

        public sealed class HereExperimentDto
        {
            public string experiment_id;
            public string title;
            public bool onboard;
            public bool valid_here;
            public float science_earned_here;
            public float science_cap_here;
            public float science_remaining_here;
            public float percent_complete;
            public float data_scale;
        }

        public sealed class NextEasyDto
        {
            public string mode;
            public string body;
            public string situation;
            public string biome;
            public int returned;
            public bool truncated;
            public List<RecommendationDto> recommendations = new List<RecommendationDto>();
        }

        public sealed class RecommendationDto
        {
            public string tier;
            public string body;
            public string situation;
            public string biome;
            public string experiment_id;
            public string title;
            public float science_remaining;
            public bool onboard;
            public string reason;
            public string action_hint;
        }

        public sealed class OpportunitiesDto
        {
            public string mode;
            public string scope;
            public List<BodyOpportunityDto> bodies = new List<BodyOpportunityDto>();
        }

        public sealed class BodyOpportunityDto
        {
            public string body;
            public int biome_count;
            public float earned_total;
            public float estimated_total_cap;
            public float remaining_estimated;
            public List<SituationOpportunityDto> situations = new List<SituationOpportunityDto>();
        }

        public sealed class SituationOpportunityDto
        {
            public string situation;
            public bool valid_for_body;
            public bool biome_split;
            public int biomes_touched;
            public int biomes_total;
            public float earned;
            public float estimated_cap;
            public float remaining_estimated;
        }

        public static TotalsDto Totals()
        {
            var dto = new TotalsDto { mode = ResolveMode() };
            if (ResearchAndDevelopment.Instance == null) return dto;

            var byBody = new Dictionary<string, BodyTotalsDto>(StringComparer.Ordinal);
            foreach (ScienceSubject subject in ResearchAndDevelopment.GetSubjects())
            {
                if (subject == null) continue;

                dto.total_earned += subject.science;
                if (subject.science > 0f) dto.subjects_touched++;

                string body = ParseSubject(subject.id).body;
                if (!byBody.TryGetValue(body, out BodyTotalsDto bodyDto))
                {
                    bodyDto = new BodyTotalsDto { body = body };
                    byBody[body] = bodyDto;
                }

                bodyDto.earned += subject.science;
                bodyDto.earned_cap_for_touched += subject.scienceCap;
                if (subject.science > 0f) bodyDto.subjects_touched++;
            }

            dto.per_body.AddRange(byBody.Values);
            dto.per_body.Sort((a, b) => b.earned.CompareTo(a.earned));
            return dto;
        }

        public static EarnedDto Earned(string body, string experimentId, int limit)
        {
            if (limit <= 0) limit = 100;
            if (limit > 500) limit = 500;

            var dto = new EarnedDto { mode = ResolveMode() };
            if (ResearchAndDevelopment.Instance == null) return dto;

            string bodyFilter = body ?? "";
            string experimentFilter = experimentId ?? "";
            var matched = new List<SubjectDto>();
            foreach (ScienceSubject subject in ResearchAndDevelopment.GetSubjects())
            {
                if (subject == null || subject.science <= 0f) continue;
                ParsedSubject parsed = ParseSubject(subject.id);
                if (bodyFilter.Length > 0 && !string.Equals(parsed.body, bodyFilter, StringComparison.Ordinal)) continue;
                if (experimentFilter.Length > 0 && !string.Equals(parsed.experimentId, experimentFilter, StringComparison.Ordinal)) continue;
                matched.Add(ToSubjectDto(subject, parsed));
            }

            matched.Sort((a, b) => b.science_earned.CompareTo(a.science_earned));
            dto.total_subjects = matched.Count;
            int take = Math.Min(limit, matched.Count);
            for (int i = 0; i < take; i++) dto.subjects.Add(matched[i]);
            dto.returned = dto.subjects.Count;
            dto.truncated = matched.Count > dto.returned;
            return dto;
        }

        public static HereDto Here(Vessel vessel)
        {
            if (vessel == null) throw new InvalidOperationException("No active vessel");
            CelestialBody body = vessel.mainBody ?? throw new InvalidOperationException("Active vessel has no main body");
            ExperimentSituations situation = ScienceUtil.GetExperimentSituation(vessel);
            string biome = ResolveBiome(vessel, body, situation);

            var dto = new HereDto
            {
                mode = ResolveMode(),
                body = body.bodyName,
                situation = situation.ToString(),
                biome = biome ?? "",
            };

            HashSet<string> onboard = OnboardExperimentIds(vessel);
            foreach (ScienceExperiment experiment in Experiments())
            {
                bool valid = experiment.IsAvailableWhile(situation, body);
                bool biomeRelevant = experiment.BiomeIsRelevantWhile(situation);
                dto.biome_relevant |= valid && biomeRelevant;
                string subjectBiome = biomeRelevant ? (biome ?? "") : "";
                ScienceSubject subject = LookupSubject(experiment.id, body.bodyName, situation, subjectBiome);
                float earned = subject?.science ?? 0f;
                float cap = subject?.scienceCap ?? EstimateCap(experiment, body, situation);

                dto.experiments.Add(new HereExperimentDto
                {
                    experiment_id = experiment.id,
                    title = experiment.experimentTitle ?? experiment.id,
                    onboard = onboard.Contains(experiment.id),
                    valid_here = valid,
                    science_earned_here = earned,
                    science_cap_here = cap,
                    science_remaining_here = Math.Max(0f, cap - earned),
                    percent_complete = cap > 0f ? 100f * earned / cap : 0f,
                    data_scale = experiment.dataScale,
                });
            }

            dto.experiments.Sort((a, b) =>
            {
                int aw = (a.onboard && a.valid_here ? 4 : 0) + (a.valid_here ? 2 : 0) + (a.science_remaining_here > 0f ? 1 : 0);
                int bw = (b.onboard && b.valid_here ? 4 : 0) + (b.valid_here ? 2 : 0) + (b.science_remaining_here > 0f ? 1 : 0);
                if (aw != bw) return bw.CompareTo(aw);
                return b.science_remaining_here.CompareTo(a.science_remaining_here);
            });
            return dto;
        }

        public static NextEasyDto NextEasy(Vessel vessel, int limit)
        {
            if (limit <= 0) limit = 10;
            if (limit > 50) limit = 50;
            if (vessel == null) throw new InvalidOperationException("No active vessel");
            CelestialBody body = vessel.mainBody ?? throw new InvalidOperationException("Active vessel has no main body");
            ExperimentSituations currentSituation = ScienceUtil.GetExperimentSituation(vessel);
            string currentBiome = ResolveBiome(vessel, body, currentSituation);
            HashSet<string> onboard = OnboardExperimentIds(vessel);

            var all = new List<RecommendationDto>();
            foreach (ScienceExperiment experiment in Experiments())
            {
                if (!onboard.Contains(experiment.id)) continue;
                AddCurrentRecommendation(all, experiment, body, currentSituation, currentBiome);
                AddSameBodyRecommendations(all, experiment, body, currentSituation, currentBiome);
            }

            all.Sort(CompareRecommendations);
            var dto = new NextEasyDto
            {
                mode = ResolveMode(),
                body = body.bodyName,
                situation = currentSituation.ToString(),
                biome = currentBiome ?? "",
                truncated = all.Count > limit,
            };
            for (int i = 0; i < Math.Min(limit, all.Count); i++) dto.recommendations.Add(all[i]);
            dto.returned = dto.recommendations.Count;
            return dto;
        }

        public static OpportunitiesDto Opportunities(string scope)
        {
            var dto = new OpportunitiesDto
            {
                mode = ResolveMode(),
                scope = scope ?? "current_soi_tree",
            };
            if (FlightGlobals.Bodies == null) return dto;

            List<CelestialBody> bodies = ResolveScope(scope);
            EarnedIndex earned = BuildEarnedIndex();
            List<ScienceExperiment> experiments = Experiments();

            foreach (CelestialBody body in bodies)
            {
                if (body != null) dto.bodies.Add(BuildBodyOpportunity(body, experiments, earned));
            }
            dto.bodies.Sort((a, b) => b.remaining_estimated.CompareTo(a.remaining_estimated));
            return dto;
        }

        public static string ResolveMode()
        {
            try
            {
                Game game = HighLogic.CurrentGame;
                if (game == null) return "unknown";
                switch (game.Mode)
                {
                    case Game.Modes.CAREER: return "career";
                    case Game.Modes.SCIENCE_SANDBOX: return "science";
                    case Game.Modes.SANDBOX: return "sandbox";
                    default: return game.Mode.ToString().ToLowerInvariant();
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private static void AddCurrentRecommendation(List<RecommendationDto> recommendations, ScienceExperiment experiment,
            CelestialBody body, ExperimentSituations situation, string biome)
        {
            if (!experiment.IsAvailableWhile(situation, body)) return;
            string subjectBiome = experiment.BiomeIsRelevantWhile(situation) ? (biome ?? "") : "";
            float remaining = RemainingScience(experiment, body, situation, subjectBiome);
            if (remaining <= 0.01f) return;
            recommendations.Add(new RecommendationDto
            {
                tier = "runnable_now",
                body = body.bodyName,
                situation = situation.ToString(),
                biome = subjectBiome,
                experiment_id = experiment.id,
                title = experiment.experimentTitle ?? experiment.id,
                science_remaining = remaining,
                onboard = true,
                reason = "Onboard experiment is valid at the active vessel's current location.",
                action_hint = "Run " + (experiment.experimentTitle ?? experiment.id) + " now.",
            });
        }

        private static void AddSameBodyRecommendations(List<RecommendationDto> recommendations, ScienceExperiment experiment,
            CelestialBody body, ExperimentSituations currentSituation, string currentBiome)
        {
            foreach (ExperimentSituations situation in AllSituations)
            {
                if (!experiment.IsAvailableWhile(situation, body)) continue;

                bool biomeRelevant = experiment.BiomeIsRelevantWhile(situation);
                if (situation == currentSituation && !biomeRelevant) continue;
                IEnumerable<string> biomes = biomeRelevant ? BodyBiomes(body) : new[] { "" };
                foreach (string biome in biomes)
                {
                    string normalizedBiome = biome ?? "";
                    if (situation == currentSituation &&
                        string.Equals(normalizedBiome, currentBiome ?? "", StringComparison.Ordinal)) continue;

                    float remaining = RemainingScience(experiment, body, situation, normalizedBiome);
                    if (remaining <= 0.01f) continue;

                    string tier = situation == currentSituation || IsLocalSituation(currentSituation, situation) ? "same_body_local" : "same_body";
                    recommendations.Add(new RecommendationDto
                    {
                        tier = tier,
                        body = body.bodyName,
                        situation = situation.ToString(),
                        biome = normalizedBiome,
                        experiment_id = experiment.id,
                        title = experiment.experimentTitle ?? experiment.id,
                        science_remaining = remaining,
                        onboard = true,
                        reason = tier == "same_body_local"
                            ? "Onboard experiment has nearby same-body science remaining."
                            : "Onboard experiment has same-body science remaining.",
                        action_hint = BuildActionHint(situation, normalizedBiome, currentBiome),
                    });
                }
            }
        }

        private static int CompareRecommendations(RecommendationDto a, RecommendationDto b)
        {
            int tier = TierRank(a.tier).CompareTo(TierRank(b.tier));
            if (tier != 0) return tier;
            int remaining = b.science_remaining.CompareTo(a.science_remaining);
            if (remaining != 0) return remaining;
            int exp = string.Compare(a.experiment_id, b.experiment_id, StringComparison.Ordinal);
            if (exp != 0) return exp;
            return string.Compare(a.biome, b.biome, StringComparison.Ordinal);
        }

        private static int TierRank(string tier)
        {
            switch (tier)
            {
                case "runnable_now": return 0;
                case "same_body_local": return 1;
                case "same_body": return 2;
                default: return 9;
            }
        }

        private static bool IsLocalSituation(ExperimentSituations current, ExperimentSituations candidate)
        {
            if ((current == ExperimentSituations.SrfLanded || current == ExperimentSituations.SrfSplashed) &&
                (candidate == ExperimentSituations.SrfLanded || candidate == ExperimentSituations.SrfSplashed ||
                 candidate == ExperimentSituations.FlyingLow)) return true;
            if ((current == ExperimentSituations.FlyingLow || current == ExperimentSituations.FlyingHigh) &&
                (candidate == ExperimentSituations.FlyingLow || candidate == ExperimentSituations.FlyingHigh)) return true;
            if ((current == ExperimentSituations.InSpaceLow || current == ExperimentSituations.InSpaceHigh) &&
                (candidate == ExperimentSituations.InSpaceLow || candidate == ExperimentSituations.InSpaceHigh)) return true;
            return false;
        }

        private static string BuildActionHint(ExperimentSituations situation, string biome, string currentBiome)
        {
            string where = biome.Length > 0 && !string.Equals(biome, currentBiome ?? "", StringComparison.Ordinal)
                ? " over " + biome
                : "";
            switch (situation)
            {
                case ExperimentSituations.SrfLanded: return "Land" + where + " and run the experiment.";
                case ExperimentSituations.SrfSplashed: return "Splash down" + where + " and run the experiment.";
                case ExperimentSituations.FlyingLow: return "Fly low" + where + " and run the experiment.";
                case ExperimentSituations.FlyingHigh: return "Fly high" + where + " and run the experiment.";
                case ExperimentSituations.InSpaceLow: return "Enter low space and run the experiment.";
                case ExperimentSituations.InSpaceHigh: return "Enter high space and run the experiment.";
                default: return "Move to " + situation + " and run the experiment.";
            }
        }

        private static List<ScienceExperiment> Experiments()
        {
            var experiments = new List<ScienceExperiment>();
            if (ResearchAndDevelopment.Instance == null) return experiments;
            foreach (string id in ResearchAndDevelopment.GetExperimentIDs())
            {
                ScienceExperiment experiment = ResearchAndDevelopment.GetExperiment(id);
                if (experiment != null) experiments.Add(experiment);
            }
            return experiments;
        }

        private static string ResolveBiome(Vessel vessel, CelestialBody body, ExperimentSituations situation)
        {
            if (situation == ExperimentSituations.SrfLanded || situation == ExperimentSituations.SrfSplashed)
            {
                if (!string.IsNullOrEmpty(vessel.landedAt)) return vessel.landedAt;
            }

            try { return ScienceUtil.GetExperimentBiome(body, vessel.latitude, vessel.longitude); }
            catch { return ""; }
        }

        private static HashSet<string> OnboardExperimentIds(Vessel vessel)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (vessel.parts == null) return set;
            foreach (Part part in vessel.parts)
            {
                if (part?.Modules == null) continue;
                foreach (PartModule module in part.Modules)
                {
                    var science = module as ModuleScienceExperiment;
                    if (science != null && !string.IsNullOrEmpty(science.experimentID)) set.Add(science.experimentID);
                }
            }
            return set;
        }

        private static float RemainingScience(ScienceExperiment experiment, CelestialBody body, ExperimentSituations situation, string biome)
        {
            ScienceSubject subject = LookupSubject(experiment.id, body.bodyName, situation, biome);
            float earned = subject?.science ?? 0f;
            float cap = subject?.scienceCap ?? EstimateCap(experiment, body, situation);
            return Math.Max(0f, cap - earned);
        }

        private static ScienceSubject LookupSubject(string experimentId, string bodyName, ExperimentSituations situation, string biome)
        {
            string id = BuildSubjectId(experimentId, bodyName, situation, biome);
            if (ResearchAndDevelopment.Instance == null) return null;
            foreach (ScienceSubject subject in ResearchAndDevelopment.GetSubjects())
            {
                if (subject != null && string.Equals(subject.id, id, StringComparison.Ordinal)) return subject;
            }
            return null;
        }

        private static string BuildSubjectId(string experimentId, string bodyName, ExperimentSituations situation, string biome)
        {
            string normalizedBiome = biome ?? "";
            if (normalizedBiome.Length > 0) normalizedBiome = normalizedBiome.Replace(" ", "");
            return experimentId + "@" + bodyName + situation + normalizedBiome;
        }

        private static float SituationMultiplier(CelestialBody body, ExperimentSituations situation)
        {
            CelestialBodyScienceParams values = body?.scienceValues;
            if (values == null) return 0f;
            switch (situation)
            {
                case ExperimentSituations.SrfLanded: return values.LandedDataValue;
                case ExperimentSituations.SrfSplashed: return values.SplashedDataValue;
                case ExperimentSituations.FlyingLow: return values.FlyingLowDataValue;
                case ExperimentSituations.FlyingHigh: return values.FlyingHighDataValue;
                case ExperimentSituations.InSpaceLow: return values.InSpaceLowDataValue;
                case ExperimentSituations.InSpaceHigh: return values.InSpaceHighDataValue;
                default: return 0f;
            }
        }

        private static float EstimateCap(ScienceExperiment experiment, CelestialBody body, ExperimentSituations situation)
        {
            return experiment.scienceCap * experiment.dataScale * SituationMultiplier(body, situation);
        }

        private static List<CelestialBody> ResolveScope(string scope)
        {
            var list = new List<CelestialBody>();
            if (FlightGlobals.Bodies == null) return list;
            string requested = scope ?? "current_soi_tree";

            if (string.Equals(requested, "all", StringComparison.OrdinalIgnoreCase))
            {
                list.AddRange(FlightGlobals.Bodies);
                return list;
            }

            if (!string.Equals(requested, "current_soi_tree", StringComparison.OrdinalIgnoreCase))
            {
                foreach (CelestialBody body in FlightGlobals.Bodies)
                {
                    if (body != null && string.Equals(body.bodyName, requested, StringComparison.Ordinal))
                    {
                        list.Add(body);
                        return list;
                    }
                }
                return list;
            }

            CelestialBody current = FlightGlobals.ActiveVessel?.mainBody;
            if (current == null)
            {
                list.AddRange(FlightGlobals.Bodies);
                return list;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(CelestialBody body)
            {
                if (body != null && seen.Add(body.bodyName)) list.Add(body);
            }

            Add(current);
            if (current.referenceBody != null && current.referenceBody != current) Add(current.referenceBody);
            if (current.orbitingBodies != null) foreach (CelestialBody child in current.orbitingBodies) Add(child);
            if (current.referenceBody?.orbitingBodies != null)
                foreach (CelestialBody sibling in current.referenceBody.orbitingBodies) Add(sibling);
            return list;
        }

        private sealed class EarnedIndex
        {
            public readonly Dictionary<string, Dictionary<ExperimentSituations, Dictionary<string, Dictionary<string, float>>>> earned
                = new Dictionary<string, Dictionary<ExperimentSituations, Dictionary<string, Dictionary<string, float>>>>(StringComparer.Ordinal);
        }

        private static EarnedIndex BuildEarnedIndex()
        {
            var index = new EarnedIndex();
            if (ResearchAndDevelopment.Instance == null) return index;

            foreach (ScienceSubject subject in ResearchAndDevelopment.GetSubjects())
            {
                if (subject == null || subject.science <= 0f) continue;
                ParsedSubject parsed = ParseSubject(subject.id);
                if (parsed.situation == null) continue;

                if (!index.earned.TryGetValue(parsed.body, out var situationMap))
                {
                    situationMap = new Dictionary<ExperimentSituations, Dictionary<string, Dictionary<string, float>>>();
                    index.earned[parsed.body] = situationMap;
                }

                ExperimentSituations situation = parsed.situation.Value;
                if (!situationMap.TryGetValue(situation, out var biomeMap))
                {
                    biomeMap = new Dictionary<string, Dictionary<string, float>>(StringComparer.Ordinal);
                    situationMap[situation] = biomeMap;
                }

                string biome = parsed.biome ?? "";
                if (!biomeMap.TryGetValue(biome, out var experimentMap))
                {
                    experimentMap = new Dictionary<string, float>(StringComparer.Ordinal);
                    biomeMap[biome] = experimentMap;
                }

                experimentMap.TryGetValue(parsed.experimentId, out float current);
                experimentMap[parsed.experimentId] = current + subject.science;
            }

            return index;
        }

        private static BodyOpportunityDto BuildBodyOpportunity(CelestialBody body, List<ScienceExperiment> experiments, EarnedIndex earned)
        {
            var dto = new BodyOpportunityDto
            {
                body = body.bodyName,
                biome_count = body.BiomeMap?.Attributes?.Length ?? 0,
            };

            earned.earned.TryGetValue(body.bodyName, out var bodyEarned);

            foreach (ExperimentSituations situation in AllSituations)
            {
                float multiplier = SituationMultiplier(body, situation);
                bool valid = multiplier > 0f;
                bool biomeSplit = false;
                float estimatedCap = 0f;

                foreach (ScienceExperiment experiment in experiments)
                {
                    if (!experiment.IsAvailableWhile(situation, body)) continue;
                    bool split = experiment.BiomeIsRelevantWhile(situation) && dto.biome_count > 0;
                    biomeSplit |= split;
                    int biomeCount = split ? dto.biome_count : 1;
                    estimatedCap += experiment.scienceCap * experiment.dataScale * multiplier * biomeCount;
                }

                float earnedHere = 0f;
                int biomesTouched = 0;
                if (bodyEarned != null && bodyEarned.TryGetValue(situation, out var biomeMap))
                {
                    foreach (var biomeEntry in biomeMap)
                    {
                        float biomeEarned = 0f;
                        foreach (float value in biomeEntry.Value.Values) biomeEarned += value;
                        earnedHere += biomeEarned;
                        if (biomeEarned > 0f) biomesTouched++;
                    }
                }

                var situationDto = new SituationOpportunityDto
                {
                    situation = situation.ToString(),
                    valid_for_body = valid,
                    biome_split = biomeSplit,
                    biomes_touched = biomesTouched,
                    biomes_total = biomeSplit ? dto.biome_count : (valid ? 1 : 0),
                    earned = earnedHere,
                    estimated_cap = estimatedCap,
                    remaining_estimated = Math.Max(0f, estimatedCap - earnedHere),
                };
                dto.situations.Add(situationDto);
                dto.earned_total += earnedHere;
                dto.estimated_total_cap += estimatedCap;
            }

            dto.remaining_estimated = Math.Max(0f, dto.estimated_total_cap - dto.earned_total);
            return dto;
        }

        private static IEnumerable<string> BodyBiomes(CelestialBody body)
        {
            if (body?.BiomeMap?.Attributes == null || body.BiomeMap.Attributes.Length == 0)
            {
                yield return "";
                yield break;
            }

            foreach (CBAttributeMapSO.MapAttribute attribute in body.BiomeMap.Attributes)
            {
                if (!string.IsNullOrEmpty(attribute.name)) yield return attribute.name;
            }
        }

        private static SubjectDto ToSubjectDto(ScienceSubject subject, ParsedSubject parsed)
        {
            return new SubjectDto
            {
                subject_id = subject.id,
                title = subject.title,
                body = parsed.body,
                situation = parsed.situation?.ToString() ?? "",
                biome = parsed.biome ?? "",
                experiment_id = parsed.experimentId,
                science_earned = subject.science,
                science_cap = subject.scienceCap,
                science_remaining = Math.Max(0f, subject.scienceCap - subject.science),
                percent_complete = subject.scienceCap > 0f ? 100f * subject.science / subject.scienceCap : 0f,
            };
        }

        private struct ParsedSubject
        {
            public string experimentId;
            public string body;
            public ExperimentSituations? situation;
            public string biome;
        }

        private static ParsedSubject ParseSubject(string subjectId)
        {
            var parsed = new ParsedSubject { experimentId = "", body = "?", biome = "" };
            if (string.IsNullOrEmpty(subjectId)) return parsed;

            int at = subjectId.IndexOf('@');
            parsed.experimentId = at < 0 ? subjectId : subjectId.Substring(0, at);
            if (at < 0 || at + 1 >= subjectId.Length || FlightGlobals.Bodies == null) return parsed;

            string tail = subjectId.Substring(at + 1);
            string bestBody = "?";
            int bestLength = 0;
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                string name = body?.bodyName;
                if (!string.IsNullOrEmpty(name) && tail.StartsWith(name, StringComparison.Ordinal) && name.Length > bestLength)
                {
                    bestBody = name;
                    bestLength = name.Length;
                }
            }
            parsed.body = bestBody;
            if (bestLength <= 0) return parsed;

            string rest = tail.Substring(bestLength);
            foreach (ExperimentSituations situation in AllSituations)
            {
                string name = situation.ToString();
                if (!rest.StartsWith(name, StringComparison.Ordinal)) continue;
                parsed.situation = situation;
                parsed.biome = rest.Length > name.Length ? rest.Substring(name.Length) : "";
                return parsed;
            }

            return parsed;
        }
    }
}
