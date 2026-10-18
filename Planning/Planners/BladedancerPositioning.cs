using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Where a Bladedancer (anyone with Blade Dance) stands. The archetype wants to be in melee with as many
    /// enemies as possible, as often as possible (Blade Dance hits everything next to her, and being in melee makes
    /// her a poor target for ranged attacks), and never next to allies, since Blade Dance's attacks pick random
    /// adjacent targets, allies included. Movement is cheap for her, so long walks are fine.
    ///  - TileBonus: added to melee attack tiles and Death From Above landings (+ per adjacent enemy, - per ally).
    ///  - PlanStepClear: allies next to her block Blade Dance; step to a reachable tile touching enemies and no
    ///    allies, then Blade Dance.
    ///  - PlanEngagementMove: with MP left at the end of a plan, move to the reachable tile touching the most
    ///    enemies and no allies. Skipped at low HP, and never along a path that provokes attacks of opportunity.
    /// </summary>
    public static class BladedancerPositioning
    {
        private const string BLADE_DANCE = "e955823f54d24088ae1fdefe88d3684d";  // ReaperBladeDanceAbility
        public const float EnemyBonus = 40f;
        public const float AllyPenalty = 80f;
        private const float AdjacentTiles = 1f;        // Blade Dance reaches creatures within 1 cell
        private const float PathCostPerTile = 2f;
        private const float ProvokedAttackPenalty = 200f;
        private const float EngageMinHP = 40f;

        public static bool IsBladeDance(AbilityData ability)
            => ability?.Blueprint?.AssetGuid?.ToString() == BLADE_DANCE;

        public static bool IsBladedancer(BaseUnitEntity unit)
        {
            var raw = unit?.Abilities?.RawFacts;
            if (raw == null) return false;
            foreach (var fact in raw)
                if (IsBladeDance(fact?.Data)) return true;
            return false;
        }

        /// <summary>Score for standing on pos: + per adjacent enemy, - per adjacent ally.</summary>
        public static float TileBonus(BaseUnitEntity unit, Vector3 pos, List<BaseUnitEntity> enemies, List<BaseUnitEntity> allies)
        {
            Count(unit, pos, enemies, allies, out int e, out int a);
            return e * EnemyBonus - a * AllyPenalty;
        }

        private static void Count(BaseUnitEntity unit, Vector3 pos, List<BaseUnitEntity> enemies, List<BaseUnitEntity> allies,
            out int adjacentEnemies, out int adjacentAllies)
        {
            adjacentEnemies = adjacentAllies = 0;
            if (enemies != null)
                foreach (var e in enemies)
                    if (e != null && e.IsConscious && !e.LifeState.IsDead && CombatAPI.GetDistanceInTiles(pos, e) <= AdjacentTiles)
                        adjacentEnemies++;
            if (allies != null)
                foreach (var a in allies)
                    if (a != null && a != unit && a.IsConscious && !a.LifeState.IsDead && CombatAPI.GetDistanceInTiles(pos, a) <= AdjacentTiles)
                        adjacentAllies++;
        }

        /// <summary>
        /// Blade Dance blocked by an adjacent ally: move to a reachable tile touching enough enemies and no allies,
        /// then Blade Dance (which ends her movement). Only from where she stands now, with no move planned yet.
        /// Returns the move and the Blade Dance, or null.
        /// </summary>
        public static List<PlannedAction> PlanStepClear(Situation situation, List<PlannedAction> actions, ref float remainingAP,
            ref float remainingMP, string roleName)
        {
            var unit = situation?.Unit;
            if (unit == null || remainingMP < 1f || !situation.CanMove) return null;
            try
            {
                if (actions != null)
                    foreach (var a in actions)
                        if (a.Type == ActionType.Move || IsBladeDance(a.Ability)) return null;

                var bladeDance = FindBladeDance(situation);
                if (bladeDance == null) return null;
                float cost = CombatAPI.GetAbilityAPCost(bladeDance);
                if (cost > remainingAP + 0.01f) return null;

                var aoe = AIConfig.GetAoEConfig();
                int maxAllies = aoe?.MaxPlayerAlliesHit ?? 0;
                int minEnemies = Math.Max(1, aoe?.SelfAoeMinAdjacentEnemies ?? 1);

                Count(unit, unit.Position, situation.Enemies, situation.Allies, out int hereEnemies, out int hereAllies);
                if (hereAllies <= maxAllies) return null;  // not blocked by allies: the normal Blade Dance rule decides

                if (!FindBestTile(situation, remainingMP, maxAllies, minEnemies, out var tile, out int enemies, out float pathCost)) return null;

                remainingMP = 0f;  // Blade Dance clears MP
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Blade Dance blocked by {hereAllies} adjacent allies: stepping to " +
                    $"({tile.x:F1}, {tile.z:F1}) next to {enemies} enemies and no allies, then Blade Dance");
                return new List<PlannedAction>
                {
                    PlannedAction.Move(tile, $"Step clear of allies for Blade Dance ({enemies} enemies)"),
                    PlannedAction.Buff(bladeDance, unit, $"Self-AoE attack hitting {enemies} enemies", cost)
                };
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[Bladedancer] step clear failed for {unit.CharacterName}");
                return null;
            }
        }

        /// <summary>
        /// End of plan with MP left: move to the reachable tile touching the most enemies and no allies, if that is
        /// better than where she stands. Inserted before any turn-ending ability.
        /// </summary>
        public static void PlanEngagementMove(Situation situation, List<PlannedAction> actions, ref float remainingMP, string roleName)
        {
            var unit = situation?.Unit;
            if (unit == null || actions == null || remainingMP < 1f || !situation.CanMove) return;
            if (situation.HPPercent < EngageMinHP || !IsBladedancer(unit)) return;
            try
            {
                // Blade Dance ends her movement, and a move already planned is the plan's own positioning
                foreach (var a in actions)
                    if (a.Type == ActionType.Move || IsBladeDance(a.Ability)) return;

                Count(unit, unit.Position, situation.Enemies, situation.Allies, out int hereEnemies, out int hereAllies);
                float hereScore = hereEnemies * EnemyBonus - hereAllies * AllyPenalty;

                if (!FindBestTile(situation, remainingMP, 0, 1, out var tile, out int enemies, out float pathCost)) return;
                float score = enemies * EnemyBonus - pathCost * PathCostPerTile;
                if (score < hereScore + EnemyBonus - 0.01f) return;  // only for at least one more enemy (or shedding allies)

                int insertAt = actions.Count;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Ability != null && AbilityDatabase.IsTurnEnding(actions[i].Ability)) { insertAt = i; break; }
                if (insertAt > 0 && actions[insertAt - 1].Type == ActionType.EndTurn) insertAt--;
                actions.Insert(insertAt, PlannedAction.Move(tile, $"Engage {enemies} enemies (Bladedancer)"));
                remainingMP -= pathCost;
                Log.Planning.Info($"[{roleName}] Bladedancer engagement move to ({tile.x:F1}, {tile.z:F1}): " +
                    $"{enemies} enemies adjacent, no allies (here: {hereEnemies} enemies, {hereAllies} allies)");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[Bladedancer] engagement move failed for {unit.CharacterName}");
            }
        }

        /// <summary>
        /// Reachable tile with at least minEnemies and at most maxAllies adjacent, most enemies first, then the
        /// shortest walk. Skips paths that provoke attacks of opportunity and hazard tiles.
        /// </summary>
        private static bool FindBestTile(Situation situation, float mp, int maxAllies, int minEnemies,
            out Vector3 best, out int bestEnemies, out float bestPath)
        {
            best = default;
            bestEnemies = 0;
            bestPath = 0f;
            var unit = situation.Unit;
            var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, mp);
            if (tiles == null || tiles.Count == 0) return false;

            bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);
            float bestScore = float.MinValue;
            foreach (var kvp in tiles)
            {
                var cell = kvp.Value;
                if (!(cell.Node is CustomGridNodeBase node) || !cell.IsCanStand) continue;
                if (cell.ProvokedAttacks > 0 || cell.Length > mp + 0.01f) continue;
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node)) continue;
                var pos = node.Vector3Position;
                if (avoidHazard && CombatAPI.IsPositionInHazardZone(pos, unit)) continue;

                Count(unit, pos, situation.Enemies, situation.Allies, out int e, out int a);
                if (e < minEnemies || a > maxAllies) continue;
                float score = e * EnemyBonus - cell.Length * PathCostPerTile;
                if (score <= bestScore) continue;
                bestScore = score;
                best = pos;
                bestEnemies = e;
                bestPath = cell.Length;
            }
            return bestScore > float.MinValue;
        }

        private static AbilityData FindBladeDance(Situation situation)
        {
            foreach (var a in situation.AvailableAttacks)
                if (IsBladeDance(a)) return a;
            foreach (var a in CombatAPI.GetAvailableAbilities(situation.Unit))
                if (IsBladeDance(a)) return a;
            return null;
        }
    }
}
