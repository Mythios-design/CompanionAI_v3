using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Big cones that come out of the caster (Cassia's Lidless Stare): find the tile and direction that put the
    /// most enemies in the cone without breaking the friendly-fire limit, and use it ahead of buffs when it hits
    /// enough enemies. Moving first is allowed because attacking usually ends movement anyway.
    /// </summary>
    public static class ConeStarePlanner
    {
        public const string LIDLESS_STARE = "b932b545f5d8460ab562b0003294e775";
        private static readonly HashSet<string> ConeGuids = new HashSet<string> { LIDLESS_STARE };

        /// <summary>Enemies the cone must cover before it's worth more than the character's buffs.</summary>
        public const int MinEnemies = 3;

        private const int MaxStandTiles = 30;

        public sealed class ConeShot
        {
            public CustomGridNodeBase Stand;
            public bool RequiresMove;
            public BaseUnitEntity AimAt;
            public int Enemies;
            public int Allies;
            public string Who;
        }

        public static bool IsConeAbility(AbilityData ability)
            => ability?.Blueprint != null && ConeGuids.Contains(ability.Blueprint.AssetGuid.ToString());

        /// <summary>The cone ability this unit can use right now, if any.</summary>
        public static AbilityData FindCone(BaseUnitEntity unit)
        {
            if (unit == null) return null;
            foreach (var ability in CombatAPI.GetAvailableAbilities(unit))
                if (IsConeAbility(ability)) return ability;
            return null;
        }

        /// <summary>The cone ability's range (tiles) if the unit knows one, available or not; 0 otherwise.</summary>
        public static float KnownConeRange(BaseUnitEntity unit)
        {
            try
            {
                var raw = unit?.Abilities?.RawFacts;
                if (raw == null) return 0f;
                foreach (var fact in raw)
                    if (IsConeAbility(fact?.Data)) return CombatAPI.GetAbilityRangeInTiles(fact.Data);
            }
            catch (Exception ex) { Log.Planning.Error(ex, "[ConeStare] KnownConeRange"); }
            return 0f;
        }

        /// <summary>
        /// Plans "move (optional) + cone" when the best cone hits at least MinEnemies. Returns null otherwise.
        /// </summary>
        public static List<PlannedAction> PlanCone(Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation?.Unit;
            if (unit == null || !situation.HasLivingEnemies) return null;
            var cone = FindCone(unit);
            if (cone == null) return null;
            float cost = CombatAPI.GetAbilityAPCost(cone);
            if (cost > remainingAP) return null;

            bool canMove = situation.CanMove && situation.CurrentMP > 0f && !situation.HasAttackedThisTurn;
            var shot = FindBest(situation, cone, canMove);
            if (shot == null)
            {
                Log.Planning.Info($"[{roleName}] {cone.Name}: no usable cone (friendly fire or no enemies in reach)");
                return null;
            }
            if (shot.Enemies < MinEnemies)
            {
                Log.Planning.Info($"[{roleName}] {cone.Name}: best cone hits {shot.Enemies} enemies (need {MinEnemies}) — not worth it over buffs");
                return null;
            }

            var actions = new List<PlannedAction>();
            if (shot.RequiresMove)
            {
                TeamBlackboard.Instance?.ReserveMovePosition(shot.Stand.Vector3Position);
                actions.Add(PlannedAction.Move(shot.Stand.Vector3Position, $"Move to fire {cone.Name}"));
            }
            remainingAP -= cost;
            actions.Add(PlannedAction.Attack(cone, shot.AimAt, $"{cone.Name}: {shot.Enemies} enemies ({shot.Who})", cost));
            Log.Planning.Info($"[{roleName}] {cone.Name}: {shot.Enemies} enemies, {shot.Allies} allies in cone, aim at {shot.AimAt.CharacterName}" +
                (shot.RequiresMove ? $", moving to ({shot.Stand.Vector3Position.x:F1},{shot.Stand.Vector3Position.z:F1}) first" : " from here"));
            return actions;
        }

        public static ConeShot FindBest(Situation situation, AbilityData cone, bool allowMove)
        {
            var unit = situation.Unit;
            var enemies = situation.Enemies?.Where(e => e != null && e.IsConscious && !e.LifeState.IsDead).ToList();
            if (enemies == null || enemies.Count == 0) return null;
            var allies = (situation.CombatantAllies ?? new List<BaseUnitEntity>())
                .Where(a => a != null && a != unit && a.IsConscious && !a.LifeState.IsDead).ToList();

            float range = CombatAPI.GetAbilityRangeInTiles(cone);
            if (range <= 0f) range = 10f;
            int maxAllies = AIConfig.GetAoEConfig()?.MaxPlayerAlliesHit ?? 0;
            bool durable = CombatAPI.IsDurable(unit);

            var currentNode = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            if (currentNode == null) return null;

            // Stand tiles: here, plus the reachable tiles with the most enemies within cone range.
            var stands = new List<(CustomGridNodeBase node, float moveTiles)> { (currentNode, 0f) };
            if (allowMove)
            {
                var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, situation.CurrentMP);
                bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);
                if (tiles != null)
                {
                    var ranked = new List<(CustomGridNodeBase node, float moveTiles, int inRange)>();
                    foreach (var kvp in tiles)
                    {
                        var cell = kvp.Value;
                        var node = cell.Node as CustomGridNodeBase;
                        if (node == null || node == currentNode || !cell.IsCanStand) continue;
                        if (cell.ProvokedAttacks > 0) continue;
                        var pos = node.Vector3Position;
                        if (avoidHazard && CombatAPI.IsPositionInHazardZone(pos, unit)) continue;
                        if (!BattlefieldGrid.Instance.ValidateNode(unit, node)) continue;
                        int inRange = enemies.Count(e => CombatAPI.MetersToTiles(Vector3.Distance(pos, e.Position)) <= range);
                        if (inRange < MinEnemies) continue;
                        ranked.Add((node, CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, pos)), inRange));
                    }
                    foreach (var r in ranked.OrderByDescending(r => r.inRange).ThenBy(r => r.moveTiles).Take(MaxStandTiles))
                        stands.Add((r.node, r.moveTiles));
                }
            }

            ConeShot best = null;
            float bestScore = float.MinValue;
            foreach (var (stand, moveTiles) in stands)
            {
                var standPos = stand.Vector3Position;
                int meleeReach = moveTiles > 0f && !durable ? CombatAPI.CountMeleeEnemiesThatCanReach(unit, standPos, enemies) : 0;
                foreach (var aim in enemies)
                {
                    if (CombatAPI.MetersToTiles(Vector3.Distance(standPos, aim.Position)) > range) continue;
                    var pattern = CombatAPI.GetAffectedNodes(cone, aim.Position, standPos);
                    if (pattern.IsEmpty) continue;

                    int enemyCount = 0, allyCount = 0;
                    var names = new List<string>();
                    foreach (var e in enemies)
                        if (Covers(pattern, e)) { enemyCount++; names.Add(e.CharacterName); }
                    if (enemyCount == 0) continue;
                    foreach (var a in allies)
                        if (Covers(pattern, a)) allyCount++;
                    if (allyCount > maxAllies) continue;

                    // Enemies first; then prefer not moving, not hitting allies, not standing where melee can reach.
                    float score = enemyCount * 100f - allyCount * 60f - moveTiles * 2f - meleeReach * 30f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = new ConeShot
                        {
                            Stand = stand, RequiresMove = stand != currentNode, AimAt = aim,
                            Enemies = enemyCount, Allies = allyCount, Who = string.Join(", ", names.Take(5))
                        };
                    }
                }
            }
            return best;
        }

        private static bool Covers(Kingmaker.UnitLogic.Abilities.Components.Patterns.OrientedPatternData pattern, BaseUnitEntity unit)
        {
            try
            {
                foreach (var occ in unit.GetOccupiedNodes())
                    if (occ != null && pattern.Contains(occ)) return true;
            }
            catch
            {
                // fall back to the centre tile
            }
            var node = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
            return node != null && pattern.Contains(node);
        }
    }
}
