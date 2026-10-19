using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Last check before a unit ends its turn with AP left: is there still an attack it can make? Looks at every
    /// attack the game allows right now (weapon attacks still available, free uses included; psychic powers; Blade
    /// Dance), from where the unit stands, or, for single-target attacks, after a walk that draws no attacks of
    /// opportunity. The game's own targeting and the friendly-fire rules decide; the most damaging option wins.
    /// Left to their own rules and never offered here: grenades, markers (Blood Oath), Blood Phial, Raining Blood,
    /// Tactical Knowledge, Acrobatic Artistry (weighed against this by the caller), Heroic Acts, Desperate Measures,
    /// heals, reloads, gap closers and turn-ending abilities.
    /// Each ability is offered once a turn (the caller records the returned key in tried), so a cast the game
    /// refuses can't loop.
    /// </summary>
    public static class LastChanceAttack
    {
        private const int MaxWalkTiles = 80;

        public static List<PlannedAction> Find(Situation situation, HashSet<string> tried, out string why,
            out float damage, out string triedKey)
        {
            why = null;
            damage = 0f;
            triedKey = null;
            var unit = situation?.Unit;
            if (unit == null) { why = "no unit"; return null; }
            float ap = CombatAPI.GetCurrentAP(unit);
            if (ap < 0.5f) { why = "no AP"; return null; }

            var enemies = new List<BaseUnitEntity>();
            if (situation.Enemies != null)
                foreach (var e in situation.Enemies)
                    if (e != null && e.IsConscious && !e.LifeState.IsDead && !CombatAPI.IsTargetImmuneToDamage(e, unit)) enemies.Add(e);
            if (enemies.Count == 0) { why = "no enemies left"; return null; }

            var candidates = new List<AbilityData>();
            bool weaponSpent = true;
            foreach (var a in CombatAPI.GetAvailableAbilities(unit))
            {
                if (!IsCandidate(a, ap)) continue;
                if (a.Weapon != null) weaponSpent = false;
                string guid = a.Blueprint.AssetGuid?.ToString();
                if (guid == null || tried.Contains(guid)) continue;
                candidates.Add(a);
            }
            if (candidates.Count == 0)
            {
                why = weaponSpent ? "weapon attack spent and no other attack affordable" : "every remaining attack already tried this turn";
                return null;
            }

            // From where the unit stands
            AbilityData bestAbility = null;
            BaseUnitEntity bestTarget = null;
            float bestDamage = 0f;
            foreach (var a in candidates)
            {
                if (BladedancerPositioning.IsBladeDance(a))
                {
                    if (BladeDanceUseful(unit, situation, enemies, out int around) && around * 10f > bestDamage
                        && BladedancerPositioning.BladeDanceWorthIt(situation, a, around, 1, ap, out _))
                    { bestAbility = a; bestTarget = unit; bestDamage = around * 10f; }
                    continue;
                }
                foreach (var e in enemies)
                {
                    if (!CombatAPI.CanUseAbilityOn(a, new TargetWrapper(e), out _)) continue;
                    if (!CombatHelpers.IsAttackSafeForTarget(a, unit, e, situation.Allies)) continue;
                    float dmg = Math.Max(1f, CombatAPI.EstimateDamage(a, e));
                    if (dmg <= bestDamage) continue;
                    bestDamage = dmg; bestAbility = a; bestTarget = e;
                }
            }
            if (bestAbility != null)
            {
                damage = bestDamage;
                triedKey = bestAbility.Blueprint.AssetGuid.ToString();
                return new List<PlannedAction> { Attack(bestAbility, bestTarget, unit, "from here") };
            }

            // After a walk: single-target attacks only (an area pattern's ally check is from where it stands)
            float mp = CombatAPI.GetCurrentMP(unit);
            if (mp >= 1f && situation.CanMove)
            {
                var walk = FindWalkAttack(situation, candidates, enemies, mp, out var ability, out var target, out float walkDamage);
                if (walk.HasValue)
                {
                    damage = walkDamage;
                    triedKey = ability.Blueprint.AssetGuid.ToString();
                    return new List<PlannedAction>
                    {
                        PlannedAction.Move(walk.Value, $"Last chance: walk to attack {target.CharacterName}"),
                        Attack(ability, target, unit, "after a walk"),
                    };
                }
            }
            why = mp >= 1f ? "no attack reaches an enemy from here or after a safe walk" : "no attack reaches an enemy from here (no MP)";
            return null;
        }

        private static bool IsCandidate(AbilityData a, float ap)
        {
            var bp = a?.Blueprint;
            if (bp == null) return false;
            if (CombatAPI.GetEffectiveAPCost(a) > ap + 0.01f) return false;
            bool attack = a.Weapon != null || CombatAPI.IsOffensiveAbility(a) || BladedancerPositioning.IsBladeDance(a);
            if (!attack) return false;
            if (bp.IsGrenade || CombatHelpers.IsGrenadeOrExplosive(a)) return false;
            if (bp.IsHeroicAct || bp.IsDesperateMeasure) return false;
            if (AbilityDatabase.IsReload(a) || AbilityDatabase.IsHealing(a) || AbilityDatabase.IsMarker(a)
                || AbilityDatabase.IsGapCloser(a) || AbilityDatabase.IsTurnEnding(a)) return false;
            if (BladedancerTactics.IsBloodOath(a) || BloodPhialPlanner.IsBloodPhial(a)
                || RainingBloodPlanner.IsRainingBlood(a) || TacticalKnowledgePlanner.IsTacticalKnowledge(a)
                || AcrobaticArtistryPlanner.IsAcrobaticArtistry(a)) return false;
            if (CombatAPI.IsPsychicAbility(a) && !CombatAPI.IsPsychicSafeToUse(a)) return false;
            return true;
        }

        /// <summary>Blade Dance hits random adjacent creatures, allies included: only with enemies and no allies next to her.</summary>
        private static bool BladeDanceUseful(BaseUnitEntity unit, Situation situation, List<BaseUnitEntity> enemies, out int around)
        {
            around = 0;
            foreach (var e in enemies)
                if (CombatAPI.GetDistanceInTiles(unit, e) <= 1.5f) around++;
            if (around == 0) return false;
            if (situation.Allies != null)
                foreach (var a in situation.Allies)
                    if (a != null && a != unit && a.IsConscious && CombatAPI.GetDistanceInTiles(unit, a) <= 1.5f) return false;
            return true;
        }

        private static Vector3? FindWalkAttack(Situation situation, List<AbilityData> candidates, List<BaseUnitEntity> enemies,
            float mp, out AbilityData ability, out BaseUnitEntity target, out float damage)
        {
            damage = 0f;
            ability = null;
            target = null;
            var unit = situation.Unit;
            var single = new List<AbilityData>();
            foreach (var a in candidates)
            {
                if (BladedancerPositioning.IsBladeDance(a)) continue;
                var pattern = CombatAPI.GetPatternInfo(a);
                if (pattern != null && pattern.IsValid) continue;
                single.Add(a);
            }
            if (single.Count == 0) return null;

            var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, mp);
            if (tiles == null) return null;
            bool avoidHazard = !CombatAPI.IsUnitInHazardZone(unit);
            var cells = new List<WarhammerPathAiCell>();
            foreach (var cell in tiles.Values)
                if (cell.IsCanStand && cell.ProvokedAttacks == 0 && cell.EnteredAoE == 0 && cell.Length <= mp + 0.01f) cells.Add(cell);
            cells.Sort((x, y) => x.Length.CompareTo(y.Length));

            int checkedTiles = 0;
            foreach (var cell in cells)
            {
                if (!(cell.Node is CustomGridNodeBase node)) continue;
                if (avoidHazard && CombatAPI.IsPositionInHazardZone(node.Vector3Position, unit)) continue;
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node)) continue;
                if (++checkedTiles > MaxWalkTiles) break;
                float bestDamage = 0f;
                foreach (var a in single)
                    foreach (var e in enemies)
                    {
                        var enemyNode = e.CurrentUnwalkableNode;
                        if (enemyNode == null) continue;
                        if (!a.CanTargetFromNode(node, enemyNode, new TargetWrapper(e),
                                out int _, out LosCalculations.CoverType _, out AbilityData.UnavailabilityReasonType? _)) continue;
                        float dmg = Math.Max(1f, CombatAPI.EstimateDamage(a, e));
                        if (dmg <= bestDamage) continue;
                        bestDamage = dmg; ability = a; target = e;
                    }
                if (ability != null) { damage = bestDamage; return node.Vector3Position; }  // nearest tile with any attack
            }
            return null;
        }

        private static PlannedAction Attack(AbilityData ability, BaseUnitEntity target, BaseUnitEntity unit, string how)
        {
            float cost = CombatAPI.GetEffectiveAPCost(ability);
            string reason = $"Last chance attack {how}: {ability.Name}" + (target != unit ? $" -> {target.CharacterName}" : "");
            return target == unit
                ? PlannedAction.Buff(ability, unit, reason, cost)
                : PlannedAction.Attack(ability, target, reason, cost);
        }
    }
}
