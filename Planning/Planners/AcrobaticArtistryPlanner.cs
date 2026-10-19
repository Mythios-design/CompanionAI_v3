using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Components;
using Kingmaker.UnitLogic.Parts;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Acrobatic Artistry (Bladedancer, 1 AP, does not end the turn). The game (CustomSpringAttackQueue) replays
    /// this turn's Death From Above jumps newest first: she walks back to each landing and jumps, free, back to
    /// where she took off, then walks free to her turn-start cell. Nothing in it provokes attacks of opportunity.
    /// Held out of every plan (AbilityPolicy) and offered at the end of the turn, from TurnOrchestrator next to the
    /// last-chance attack, when either:
    ///  - Replay: the replayed landings (the take-off spots) are next to living enemies and no allies, worth the
    ///    estimated Death From Above damage on those enemies; or
    ///  - Escape: HP is under 40% and the turn-start cell is safer than where she stands (fewer melee enemies can
    ///    reach it, or the same number and better cover).
    /// </summary>
    public static class AcrobaticArtistryPlanner
    {
        private const string ACROBATIC_ARTISTRY = "1798e1237504457db15655280481d549";
        private const float EscapeHP = 40f;
        private const float AdjacentTiles = 1f;

        public static bool IsAcrobaticArtistry(AbilityData ability)
        {
            var bp = ability?.Blueprint;
            if (bp == null) return false;
            if (bp.AssetGuid?.ToString() == ACROBATIC_ARTISTRY) return true;
            // Variants of the ability share the game's replay component
            return bp.GetComponent<CustomSpringAttackQueue>() != null;
        }

        /// <summary>
        /// The Acrobatic Artistry cast, or null with why. replayDamage is the estimated damage of the replayed
        /// landings (0 when there is none); escape is true when HP and the turn-start cell make it a retreat.
        /// </summary>
        public static PlannedAction Find(Situation situation, out string why, out float replayDamage, out bool escape)
        {
            why = null;
            replayDamage = 0f;
            escape = false;
            var unit = situation?.Unit;
            var ability = situation?.HeldAcrobaticArtistry;
            if (unit == null || ability == null) { why = "not available"; return null; }

            float cost = Math.Max(0f, CombatAPI.GetAbilityAPCost(ability));
            if (cost > CombatAPI.GetCurrentAP(unit) + 0.01f) { why = $"needs {cost:F0} AP"; return null; }
            if (!CombatAPI.IsAbilityAvailable(ability, out var reasons))
            {
                why = "not usable: " + (reasons != null && reasons.Count > 0 ? string.Join(", ", reasons) : "no reason given");
                return null;
            }
            var part = unit.GetOptional<UnitPartSpringAttack>();
            if (part == null) { why = "no turn-start record"; return null; }

            var enemies = LivingEnemies(situation);
            replayDamage = ReplayDamage(situation, part, enemies, out string replayWhy);

            var start = part.TurnStartPosition;
            bool away = CombatAPI.GetDistanceInTiles(start, unit.Position) >= 1f;
            string escapeWhy;
            if (situation.HPPercent >= EscapeHP) escapeWhy = $"HP {situation.HPPercent:F0}% not under {EscapeHP:F0}%";
            else if (!away) escapeWhy = "already at the turn-start cell";
            else escape = SaferAtStart(unit, start, enemies, out escapeWhy);

            if (replayDamage <= 0f && !escape)
            {
                why = $"replay: {replayWhy}; escape: {escapeWhy}";
                return null;
            }
            var target = new TargetWrapper(unit);
            if (!CombatAPI.CanUseAbilityOn(ability, target, out string reason))
            {
                why = $"game refuses the cast: {reason}";
                return null;
            }
            string what = escape ? $"escape to the turn-start cell ({escapeWhy})" : $"replay {replayWhy}";
            if (escape && replayDamage > 0f) what += $", replay {replayWhy}";
            return PlannedAction.Buff(ability, unit, $"Acrobatic Artistry: {what}", cost);
        }

        private static List<BaseUnitEntity> LivingEnemies(Situation situation)
        {
            var list = new List<BaseUnitEntity>();
            if (situation.Enemies != null)
                foreach (var e in situation.Enemies)
                    if (e != null && e.IsConscious && !e.LifeState.IsDead) list.Add(e);
            return list;
        }

        /// <summary>
        /// Damage of the replayed jumps: each lands where its original jump took off, hitting the enemies next to
        /// that spot. A landing next to an ally makes the whole replay worthless (nothing hits allies on purpose).
        /// </summary>
        private static float ReplayDamage(Situation situation, UnitPartSpringAttack part, List<BaseUnitEntity> enemies, out string why)
        {
            var entries = part.Entries;
            if (entries == null || entries.Count == 0) { why = "no Death From Above this turn"; return 0f; }
            var unit = situation.Unit;
            var dfa = FindDeathFromAbove(unit);
            float total = 0f;
            int landings = 0, hits = 0;
            foreach (var entry in entries)
            {
                if (entry == null || entry.NewPosition == entry.OldPosition) continue;
                landings++;
                if (situation.Allies != null)
                    foreach (var a in situation.Allies)
                        if (a != null && a != unit && a.IsConscious && !a.LifeState.IsDead
                            && CombatAPI.GetDistanceInTiles(entry.OldPosition, a) <= AdjacentTiles)
                        {
                            why = $"a landing is next to {a.CharacterName}";
                            return 0f;
                        }
                foreach (var e in enemies)
                {
                    if (CombatAPI.GetDistanceInTiles(entry.OldPosition, e) > AdjacentTiles) continue;
                    hits++;
                    total += dfa != null ? Math.Max(1f, CombatAPI.EstimateDamage(dfa, e)) : 1f;
                }
            }
            why = landings == 0 ? "no jump moved her" : $"{landings} landings touching {hits} enemies";
            return total;
        }

        private static AbilityData FindDeathFromAbove(BaseUnitEntity unit)
        {
            var raw = unit.Abilities?.RawFacts;
            if (raw == null) return null;
            foreach (var fact in raw)
                if (BladedancerTactics.IsDeathFromAbove(fact?.Data)) return fact.Data;
            return null;
        }

        private static bool SaferAtStart(BaseUnitEntity unit, Vector3 start, List<BaseUnitEntity> enemies, out string why)
        {
            var here = unit.CurrentUnwalkableNode;
            var startNode = start.GetNearestNodeXZ() as CustomGridNodeBase;
            if (here == null || startNode == null) { why = "no grid node"; return false; }
            int hereReach = CombatAPI.CountMeleeEnemiesThatCanReach(unit, unit.Position, enemies);
            int startReach = CombatAPI.CountMeleeEnemiesThatCanReach(unit, start, enemies);
            int hereCover = CoverPositioning.CoverScore(unit, here, enemies, out _);
            int startCover = CoverPositioning.CoverScore(unit, startNode, enemies, out _);
            why = $"melee reach {hereReach} -> {startReach}, cover {hereCover} -> {startCover}";
            return startReach < hereReach || (startReach == hereReach && startCover > hereCover);
        }
    }
}
