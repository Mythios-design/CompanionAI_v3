using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Raining Blood (Death Cult Assassin): the next single-target melee attack splashes blood in a 5-cell cone
    /// behind the target. Allies in it gain bloodblessed (+damage), enemies, the target included, bloodstained
    /// (+damage taken). Each cast also gives a Blood Phial charge back.
    /// It's used as a Blood Phial refill, not a lead-in: AbilityPolicy holds it out of the normal buff picks
    /// (Situation.HeldRainingBlood), and it's only valid while Blood Phial has no charges left, on a turn with a
    /// single-target melee attack still to make (so the splash lands).
    /// Reserve() sets its AP aside at the start of planning when she can afford it plus such an attack, so buffs
    /// and debuffs can't take it. The plan gives the reserve back before Arrange(), which puts Raining Blood right
    /// before a planned single-target melee attack; with no such attack the AP stays unspent by it.
    /// With several such attacks, the one whose cone scores best goes first: 2 per enemy, 1 per ally.
    /// The cone is approximated as up to 5 tiles behind the target, within 45 degrees of the line from the attack
    /// tile through the target. Area attacks, jumps, charges and Heroic Acts don't trigger it, so it's never put
    /// before those.
    /// </summary>
    public static class RainingBloodPlanner
    {
        private const string RAINING_BLOOD = "e299c13f2278479bbd4e23269af5657c";  // DeathCultAssassin_InnateAbility
        private const float ConeLengthTiles = 5f;
        private const float ConeHalfAngle = 45f;
        private const int EnemyWeight = 2;
        private const int AllyWeight = 1;

        public static bool IsRainingBlood(AbilityData ability)
            => ability?.Blueprint?.AssetGuid?.ToString() == RAINING_BLOOD;

        /// <summary>
        /// Sets Raining Blood's AP aside when the phial is empty and she can afford it plus a single-target melee
        /// attack. Returns the AP taken out of remainingAP; the plan adds it back before Arrange().
        /// </summary>
        public static float Reserve(Situation situation, ref float remainingAP, string roleName)
        {
            var rb = situation?.HeldRainingBlood;
            if (rb == null) return 0f;
            try
            {
                if (!PhialEmpty(situation.Unit) || AlreadyPrimed(situation.Unit, rb)) return 0f;
                float cost = CombatAPI.GetAbilityAPCost(rb);
                float attackCost = CheapestSingleTargetMelee(situation);
                if (attackCost < 0f || cost + attackCost > remainingAP + 0.01f) return 0f;
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Blood Phial empty: {cost:F0} AP reserved for Raining Blood before a single-target attack");
                return cost;
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[RainingBlood] reserve failed for {situation.Unit?.CharacterName}");
                return 0f;
            }
        }

        public static void Arrange(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var rb = situation?.HeldRainingBlood;
            if (rb == null || actions == null) return;
            try
            {
                float cost = CombatAPI.GetAbilityAPCost(rb);
                if (cost > remainingAP + 0.01f) return;
                if (!PhialEmpty(situation.Unit) || AlreadyPrimed(situation.Unit, rb)) return;

                int bestIndex = -1, bestScore = -1, bestEnemies = 0, bestAllies = 0;
                Vector3 from = situation.Unit.Position;
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a.Type == ActionType.Move && a.MoveDestination.HasValue) { from = a.MoveDestination.Value; continue; }
                    if (!IsSingleTargetMelee(a, out var target)) continue;
                    ScoreCone(situation, from, target, out int enemies, out int allies);
                    int score = enemies * EnemyWeight + allies * AllyWeight;
                    if (score <= bestScore) continue;
                    bestIndex = i; bestScore = score; bestEnemies = enemies; bestAllies = allies;
                }
                if (bestIndex < 0) return;

                var attack = actions[bestIndex];
                var action = PlannedAction.Buff(rb, situation.Unit, $"Raining Blood before {attack.Ability.Name}", cost);
                action.GroupTag = attack.GroupTag;
                actions.Insert(bestIndex, action);
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Raining Blood before {attack.Ability.Name} -> {((BaseUnitEntity)attack.Target.Entity).CharacterName}: " +
                    $"cone covers {bestEnemies} enemies (target included), {bestAllies} allies");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[RainingBlood] planning failed for {situation.Unit?.CharacterName}");
            }
        }

        /// <summary>A single-target melee attack: not an area pattern, jump, charge or Heroic Act.</summary>
        private static bool IsSingleTargetMelee(PlannedAction a, out BaseUnitEntity target)
        {
            target = a.Target?.Entity as BaseUnitEntity;
            if (a.Type != ActionType.Attack || target == null) return false;
            if (a.AllTargets != null && a.AllTargets.Count > 1) return false;
            return IsSingleTargetMelee(a.Ability);
        }

        private static bool IsSingleTargetMelee(AbilityData ability)
        {
            if (ability == null || !ability.IsMelee) return false;
            var bp = ability.Blueprint;
            if (bp == null || !bp.CanTargetEnemies || bp.IsHeroicAct || bp.IsDesperateMeasure || ability.IsCharge) return false;
            if (AbilityDatabase.IsGapCloser(ability) || AbilityDatabase.IsDangerousAoE(ability)) return false;
            var pattern = CombatAPI.GetPatternInfo(ability);
            return pattern == null || !pattern.IsValid;
        }

        /// <summary>AP of the cheapest available single-target melee attack, or -1 when there is none.</summary>
        private static float CheapestSingleTargetMelee(Situation situation)
        {
            float best = -1f;
            foreach (var a in situation.AvailableAttacks)
            {
                if (!IsSingleTargetMelee(a)) continue;
                float c = CombatAPI.GetEffectiveAPCost(a);
                if (best < 0f || c < best) best = c;
            }
            return best;
        }

        /// <summary>
        /// Blood Phial has no charges left. Read from the full ability list, since the game drops an ability with
        /// no charges from the available ones. False when she has no Blood Phial at all.
        /// </summary>
        private static bool PhialEmpty(BaseUnitEntity unit)
        {
            var raw = unit?.Abilities?.RawFacts;
            if (raw == null) return false;
            foreach (var fact in raw)
            {
                var ability = fact?.Data;
                if (!BloodPhialPlanner.IsBloodPhial(ability)) continue;
                return ability.GetAvailableForCastCount() == 0;
            }
            return false;
        }

        /// <summary>Raining Blood cast this turn and no single-target melee attack since, or its buff still on her.</summary>
        private static bool AlreadyPrimed(BaseUnitEntity unit, AbilityData rb)
        {
            if (AllyStateCache.HasBuff(unit, rb)) return true;
            var executed = TurnOrchestrator.Instance?.GetCurrentTurnState()?.ExecutedActions;
            if (executed == null) return false;
            for (int i = executed.Count - 1; i >= 0; i--)
            {
                var a = executed[i];
                if (a.WasSuccessful != true) continue;
                if (IsRainingBlood(a.Ability)) return true;
                if (IsSingleTargetMelee(a, out _)) return false;
            }
            return false;
        }

        private static void ScoreCone(Situation situation, Vector3 from, BaseUnitEntity target, out int enemies, out int allies)
        {
            enemies = 1;  // the target is always bloodstained
            allies = 0;
            Vector3 dir = target.Position - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();

            foreach (var e in situation.Enemies)
                if (e != null && e != target && e.IsConscious && InCone(target.Position, dir, e.Position)) enemies++;
            foreach (var a in situation.Allies)
                if (a != null && a.IsConscious && InCone(target.Position, dir, a.Position)) allies++;
        }

        private static bool InCone(Vector3 apex, Vector3 dir, Vector3 pos)
        {
            Vector3 v = pos - apex;
            v.y = 0f;
            float along = CombatAPI.MetersToTiles(Vector3.Dot(v, dir));
            if (along <= 0.3f || along > ConeLengthTiles + 0.5f) return false;
            return Vector3.Angle(dir, v) <= ConeHalfAngle;
        }
    }
}
