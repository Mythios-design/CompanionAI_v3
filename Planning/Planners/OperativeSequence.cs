using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// One-target turn for a shooter with Operative / Arbitrator debuffs (Thordan): set up the enemy he is about to
    /// shoot, then shoot it.
    ///   Analyse Enemies (adds exploits) -> Expose Weakness (spends them: less dodge, parry and armour)
    ///   -> Death Sentence (more crit chance, within 10 tiles) -> the shot.
    /// Runs as a last pass over a finished plan: finds the shot (a ranged weapon attack that ends weapon attacks),
    /// takes back any of these debuffs planned elsewhere, and puts them in front of the shot on its target as far as
    /// the AP allows, always keeping the shot. With less AP, Death Sentence goes first, then Analyse.
    /// A debuff already on the target is skipped, which also keeps a replan from casting it twice.
    /// AP left after that goes on Analyse Enemies for the team's next target, to set up the next turn.
    /// </summary>
    public static class OperativeSequence
    {
        private static readonly HashSet<string> AnalyseGuids = new HashSet<string>
        {
            "502b9195c84c42c5b180f3f63ed7955c",  // Analyse Enemies
            "18f968c992344aa08d873bab8ae6a7af",  // _Mob
            "aa56d3dd1b744673ba16672a1ea1cfb1",  // _OffbeatSight
            "50dd16833a7a4ffcaab972fe3fca3980",  // _Starport
        };
        private const string DEATH_SENTENCE = "6a4c3b65dff840e0aab5966ffe8aa7ba";

        private enum Kind { None, Analyse, Expose, DeathSentence }

        private static Kind KindOf(AbilityData ability)
        {
            string guid = ability?.Blueprint?.AssetGuid?.ToString();
            if (guid == null) return Kind.None;
            if (AnalyseGuids.Contains(guid)) return Kind.Analyse;
            if (guid == DEATH_SENTENCE) return Kind.DeathSentence;
            if (AbilityDatabase.IsExposeWeakness(ability)) return Kind.Expose;
            return Kind.None;
        }

        public static void Arrange(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            if (actions == null || situation?.Unit == null) return;
            try
            {
                ArrangeInternal(actions, situation, ref remainingAP, roleName);
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[Operative] Arrange failed for {situation.Unit.CharacterName}");
            }
        }

        private static void ArrangeInternal(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation.Unit;

            // The abilities this unit has right now
            AbilityData analyse = null, expose = null, sentence = null;
            foreach (var list in new[] { situation.AvailableDebuffs, situation.AvailableAttacks, situation.AvailableMarkers })
            {
                if (list == null) continue;
                foreach (var a in list)
                {
                    switch (KindOf(a))
                    {
                        case Kind.Analyse: analyse = analyse ?? a; break;
                        case Kind.Expose: expose = expose ?? a; break;
                        case Kind.DeathSentence: sentence = sentence ?? a; break;
                    }
                }
            }
            if (analyse == null && expose == null && sentence == null) return;

            // The shot
            int shotIndex = -1;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                if (a.Type != ActionType.Attack || a.Ability?.Weapon == null || a.Ability.IsMelee) continue;
                if (!(a.Target?.Entity is BaseUnitEntity) || !AbilityPolicy.EndsWeaponAttacks(a.Ability)) continue;
                shotIndex = i;
                break;
            }
            if (shotIndex < 0) return;
            var shot = actions[shotIndex];
            var target = (BaseUnitEntity)shot.Target.Entity;

            // Take back these debuffs wherever else the plan put them
            int removed = 0;
            for (int i = actions.Count - 1; i >= 0; i--)
            {
                if (KindOf(actions[i].Ability) == Kind.None) continue;
                remainingAP += actions[i].APCost;
                if (i < shotIndex) shotIndex--;
                actions.RemoveAt(i);
                removed++;
            }

            // Where he shoots from: the last move before the shot, else where he stands
            Vector3 shootFrom = unit.Position;
            for (int i = shotIndex - 1; i >= 0; i--)
            {
                if (actions[i].Type == ActionType.Move && actions[i].MoveDestination.HasValue) { shootFrom = actions[i].MoveDestination.Value; break; }
            }

            float budget = remainingAP;
            var skipped = new List<string>();
            bool useExpose = Usable(expose, target, unit, shootFrom, ref budget, skipped);
            bool useAnalyse = useExpose && Usable(analyse, target, unit, shootFrom, ref budget, skipped);
            if (!useExpose && analyse != null) skipped.Add($"{analyse.Name} (only used with Expose Weakness)");
            bool useSentence = Usable(sentence, target, unit, shootFrom, ref budget, skipped);

            var inserted = new List<PlannedAction>();
            if (useAnalyse) inserted.Add(PlannedAction.Debuff(analyse, target, $"Set up {target.CharacterName}: exploits", CombatAPI.GetAbilityAPCost(analyse)));
            if (useExpose) inserted.Add(PlannedAction.Debuff(expose, target, $"Set up {target.CharacterName}: expose", CombatAPI.GetAbilityAPCost(expose)));
            if (useSentence) inserted.Add(PlannedAction.Debuff(sentence, target, $"Set up {target.CharacterName}: death sentence", CombatAPI.GetAbilityAPCost(sentence)));
            foreach (var a in inserted) a.GroupTag = shot.GroupTag;
            actions.InsertRange(shotIndex, inserted);
            remainingAP = budget;

            // Leftover AP: exploits on the team's next target, for allies and next turn's Expose Weakness
            string extra = null;
            if (!useAnalyse && analyse != null)
            {
                var next = TeamBlackboard.Instance?.SharedTarget;
                if (next == null || next == target || !next.IsConscious) next = null;
                float before = remainingAP;
                if (next != null && Usable(analyse, next, unit, shootFrom, ref remainingAP))
                {
                    actions.Insert(shotIndex + inserted.Count + 1,
                        PlannedAction.Debuff(analyse, next, $"Exploits on next target {next.CharacterName}", before - remainingAP));
                    extra = $", then Analyse Enemies on {next.CharacterName}";
                }
            }

            if (inserted.Count > 0 || removed > 0 || extra != null || skipped.Count > 0)
            {
                var names = new List<string>();
                foreach (var a in inserted) names.Add(a.Ability.Name);
                Log.Planning.Info($"[{roleName}] Operative turn on {target.CharacterName}: " +
                    (names.Count > 0 ? string.Join(" -> ", names) + " -> " : "") + $"{shot.Ability.Name}{extra} (AP left {remainingAP:F1})" +
                    (skipped.Count > 0 ? $"; skipped: {string.Join(", ", skipped)}" : ""));
            }
        }

        /// <summary>Affordable, not already on the target, and castable on it (Death Sentence within range of the shooting tile).</summary>
        private static bool Usable(AbilityData ability, BaseUnitEntity target, BaseUnitEntity unit, Vector3 shootFrom, ref float budget, List<string> skipped = null)
        {
            if (ability == null || target == null) return false;
            float cost = CombatAPI.GetAbilityAPCost(ability);
            if (cost > budget + 0.01f) return Skip(skipped, ability, $"needs {cost:F0} AP, {budget:F0} left");
            if (AllyStateCache.HasBuff(target, ability)) return Skip(skipped, ability, "already on the target");
            if (CombatAPI.IsTargetImmuneToDamage(target, unit) && KindOf(ability) == Kind.DeathSentence) return Skip(skipped, ability, "target immune to damage");

            float range = CombatAPI.GetAbilityRangeInTiles(ability);
            float dist = CombatAPI.GetDistanceInTiles(shootFrom, target);
            if (range > 0 && range < 1000 && dist > range) return Skip(skipped, ability, $"out of range ({dist:F0} > {range:F0} tiles)");
            // Range is checked from the shooting tile above; line of sight and other rules from where he stands now
            if (CombatAPI.MetersToTiles(Vector3.Distance(shootFrom, unit.Position)) < 0.5f
                && !CombatAPI.CanUseAbilityOn(ability, new TargetWrapper(target), out string reason))
                return Skip(skipped, ability, reason);

            budget -= cost;
            return true;
        }

        private static bool Skip(List<string> skipped, AbilityData ability, string why)
        {
            skipped?.Add($"{ability.Name} ({why})");
            return false;
        }
    }
}
