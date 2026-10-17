using System;
using System.Collections.Generic;
using Kingmaker.Blueprints;
using Kingmaker.Designers.Mechanics.Facts;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
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
    /// Bladedancer rules:
    ///  - Blood Oath only goes on an enemy the Bladedancer can reach this turn (walking, counting the MP that spending
    ///    wounds gives, or with one Death From Above jump) and can probably kill. The kill is what pays: wounds back,
    ///    +1 AP and an extra attack (once per round). Once sworn, that enemy is her target until it dies. After the
    ///    round's reward, extra oaths (once per enemy) go only on hard-to-hit enemies she's about to attack.
    ///  - Death From Above is a free attack when the landing is next to enough enemies. As a plain gap closer it is
    ///    only used with more than <see cref="GapCloserReserve"/> uses left, or to reach the oath target.
    /// The MP bonus from wound spending (a common Bladedancer talent) is learned in the fight: the first time she
    /// spends wounds, the MP gained is recorded and used for later oath decisions.
    /// </summary>
    public static class BladedancerTactics
    {
        private const string BLOOD_OATH = "590c990c1d684fd09ae883754d28a8ac";

        // Point-target Death From Above variants (the Ultimate targets an enemy and is a Heroic Act)
        private static readonly HashSet<string> DeathFromAboveGuids = new HashSet<string>
        {
            "6f1b7cfb48a0450cb85ce8a8879502de",  // Death From Above
            "780c0fb08c2e4205a8f6df9b9fc2ed3b",  // Free
            "bdb0bd56d86a45dc914226ea4704f5f6",  // Spring
            "2fe0f07d130c4af2ab23a9ce75efad5a",  // Targeted
        };

        /// <summary>Uses of Death From Above kept back: only use it as a plain gap closer with more than this.</summary>
        public const int GapCloserReserve = 3;

        /// <summary>Enemies next to the landing tile before a jump is worth it as an attack.</summary>
        public const int StrikeMinEnemies = 2;

        private const float AdjacentTiles = 1.5f;
        private const int MaxLandingChecks = 12;

        /// <summary>After the round's kill reward, extra oaths only go on enemies this hard to hit (dodge or parry %).</summary>
        public const int ExtraOathMinAvoidance = 20;

        // Per combat
        // Enemies sworn by each Bladedancer, in order: the first living one is her main target
        private static readonly Dictionary<string, List<string>> _oathTargets = new Dictionary<string, List<string>>();
        // Enemies sworn this round (Blood Oath can be used several times a turn, once per enemy)
        private static readonly Dictionary<string, (int round, HashSet<string> ids)> _swornThisRound = new Dictionary<string, (int, HashSet<string>)>();
        // Round in which the oath kill reward (+1 AP and an extra attack, once per round) was earned
        private static readonly Dictionary<string, int> _rewardRound = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> _lastSworn = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> _woundMPBonus = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _mpBeforeWoundSpend = new Dictionary<string, float>();

        public static void Reset()
        {
            _oathTargets.Clear();
            _swornThisRound.Clear();
            _rewardRound.Clear();
            _lastSworn.Clear();
            _woundMPBonus.Clear();
            _mpBeforeWoundSpend.Clear();
        }

        private static int CurrentRound => Kingmaker.Game.Instance?.TurnController?.CombatRound ?? 0;

        private static HashSet<string> SwornThisRound(string casterId)
        {
            int round = CurrentRound;
            if (!_swornThisRound.TryGetValue(casterId, out var entry) || entry.round != round)
            {
                entry = (round, new HashSet<string>());
                _swornThisRound[casterId] = entry;
            }
            return entry.ids;
        }

        private static bool RewardClaimedThisRound(string casterId)
            => _rewardRound.TryGetValue(casterId, out int round) && round == CurrentRound;

        public static bool IsBloodOath(AbilityData ability) => Guid(ability) == BLOOD_OATH;

        public static bool IsDeathFromAbove(AbilityData ability)
        {
            string guid = Guid(ability);
            return guid != null && DeathFromAboveGuids.Contains(guid);
        }

        private static string Guid(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString();

        /// <summary>Uses of the ability left. The game reports -1 when it has no limit.</summary>
        private static int UsesLeft(AbilityData ability)
        {
            int uses = ability.GetAvailableForCastCount();
            return uses < 0 ? int.MaxValue : uses;
        }

        #region Tracking (called from TurnState and SituationAnalyzer)

        /// <summary>A planned action was carried out: remember a sworn oath, and the MP before a wound spend.</summary>
        public static void OnActionExecuted(BaseUnitEntity unit, PlannedAction action)
        {
            var ability = action?.Ability;
            if (unit == null || ability?.Blueprint == null) return;
            try
            {
                if (ability.Blueprint.GetComponent<AbilityResourceWounds>() != null)
                    _mpBeforeWoundSpend[unit.UniqueId] = CombatAPI.GetCurrentMP(unit);

                var target = action.Target?.Entity as BaseUnitEntity;
                if (IsBloodOath(ability) && target != null)
                {
                    if (!_oathTargets.TryGetValue(unit.UniqueId, out var list))
                        _oathTargets[unit.UniqueId] = list = new List<string>();
                    if (!list.Contains(target.UniqueId)) list.Add(target.UniqueId);
                    SwornThisRound(unit.UniqueId).Add(target.UniqueId);
                    _lastSworn[unit.UniqueId] = target.UniqueId;
                    Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: Blood Oath sworn on {target.CharacterName} ({list.Count} sworn)");
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[Bladedancer] OnActionExecuted {unit.CharacterName}");
            }
        }

        /// <summary>The game refused the cast: forget what <see cref="OnActionExecuted"/> recorded for it.</summary>
        public static void OnCastRefused(BaseUnitEntity unit, AbilityData ability)
        {
            if (unit == null) return;
            _mpBeforeWoundSpend.Remove(unit.UniqueId);
            if (!IsBloodOath(ability) || !_lastSworn.TryGetValue(unit.UniqueId, out string id)) return;
            _lastSworn.Remove(unit.UniqueId);
            if (_oathTargets.TryGetValue(unit.UniqueId, out var list)) list.Remove(id);
            SwornThisRound(unit.UniqueId).Remove(id);
            Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: Blood Oath refused, that oath target cleared");
        }

        /// <summary>After a wound spend, the next analysis sees the MP it gave; remember the bonus for this combat.</summary>
        public static void ObserveMP(BaseUnitEntity unit, float currentMP)
        {
            if (unit == null || !_mpBeforeWoundSpend.TryGetValue(unit.UniqueId, out float before)) return;
            _mpBeforeWoundSpend.Remove(unit.UniqueId);
            float gained = currentMP - before;
            if (gained <= 0.01f) return;
            _woundMPBonus.TryGetValue(unit.UniqueId, out float known);
            if (gained > known)
            {
                _woundMPBonus[unit.UniqueId] = gained;
                Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: spending wounds gives +{gained:F1} MP (learned for this combat)");
            }
        }

        private static float WoundMPBonus(BaseUnitEntity unit)
            => unit != null && _woundMPBonus.TryGetValue(unit.UniqueId, out float mp) ? mp : 0f;

        /// <summary>Her main oath target: the first living enemy she has sworn on, or null.</summary>
        public static BaseUnitEntity GetOathTarget(Situation situation)
        {
            var living = GetOathTargets(situation);
            return living.Count > 0 ? living[0] : null;
        }

        /// <summary>
        /// Living enemies she has sworn on, in the order sworn. Sworn enemies who are gone are dropped; one going down
        /// during her own turn after she attacked counts as the round's kill reward.
        /// </summary>
        public static List<BaseUnitEntity> GetOathTargets(Situation situation)
        {
            var result = new List<BaseUnitEntity>();
            var unit = situation?.Unit;
            if (unit == null || !_oathTargets.TryGetValue(unit.UniqueId, out var ids) || ids.Count == 0) return result;

            for (int i = ids.Count - 1; i >= 0; i--)
            {
                BaseUnitEntity found = null;
                foreach (var enemy in situation.Enemies)
                {
                    if (enemy != null && enemy.UniqueId == ids[i] && enemy.IsConscious && !enemy.LifeState.IsDead) { found = enemy; break; }
                }
                if (found != null) continue;

                ids.RemoveAt(i);
                // Only an approximation of who killed it: the game gives the reward to her killing blow
                var turnState = TurnOrchestrator.Instance?.GetCurrentTurnState();
                if (turnState?.Unit == unit && turnState.HasAttackedThisTurn && !RewardClaimedThisRound(unit.UniqueId))
                {
                    _rewardRound[unit.UniqueId] = CurrentRound;
                    Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: oath target down this turn, kill reward taken for the round");
                }
            }
            foreach (var id in ids)
            {
                foreach (var enemy in situation.Enemies)
                    if (enemy != null && enemy.UniqueId == id) { result.Add(enemy); break; }
            }
            return result;
        }

        /// <summary>Already sworn on this round, or still carrying her oath: Blood Oath goes on each enemy once.</summary>
        private static bool IsAlreadySworn(Situation situation, AbilityData oath, BaseUnitEntity enemy)
            => SwornThisRound(situation.Unit.UniqueId).Contains(enemy.UniqueId) || AllyStateCache.HasBuff(enemy, oath);

        #endregion

        #region Planning

        /// <summary>
        /// Phase 1.4: swear Blood Oath on a reachable, killable enemy, or jump with Death From Above to a tile next to
        /// enough enemies. Each is planned on its own; the plan is rebuilt after it with the new MP and position.
        /// </summary>
        public static List<PlannedAction> PlanOpening(Situation situation, ref float remainingAP, string roleName)
        {
            if (situation?.Unit == null) return null;
            try
            {
                var oath = PlanBloodOath(situation, ref remainingAP, roleName);
                if (oath != null) return new List<PlannedAction> { oath };

                var strike = PlanDeathFromAboveStrike(situation, ref remainingAP, roleName);
                if (strike != null) return new List<PlannedAction> { strike };
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[Bladedancer] PlanOpening {situation.Unit.CharacterName}");
            }
            return null;
        }

        /// <summary>
        /// Blood Oath can go on several enemies a turn, once each. The kill reward comes once per round, so:
        ///  - Reward not yet taken: one oath at a time, on an enemy she can reach and probably kill. While that enemy
        ///    lives, no other oath.
        ///  - Reward taken: extra oaths only on the enemy she's about to hit, when its dodge or parry is at least
        ///    <see cref="ExtraOathMinAvoidance"/>%, since the oath's attack can't be dodged or parried.
        /// Each oath costs wounds; the 70% HP gate is applied by the analyzer.
        /// </summary>
        private static PlannedAction PlanBloodOath(Situation situation, ref float remainingAP, string roleName)
        {
            AbilityData oath = null;
            foreach (var marker in situation.AvailableMarkers)
                if (IsBloodOath(marker)) { oath = marker; break; }
            if (oath == null) return null;

            float cost = CombatAPI.GetAbilityAPCost(oath);
            if (cost > remainingAP) return null;

            var living = GetOathTargets(situation);
            BaseUnitEntity target;
            string why;
            if (!RewardClaimedThisRound(situation.Unit.UniqueId))
            {
                if (living.Count > 0) return null;  // the sworn enemy still needs killing first
                target = ChooseOathTarget(situation, oath, cost, out why);
            }
            else
            {
                target = ChooseExtraOathTarget(situation, oath, out why);
            }
            if (target == null)
            {
                Log.Planning.Info($"[{roleName}] Blood Oath held: {why}");
                return null;
            }
            if (!CombatAPI.CanUseAbilityOn(oath, new TargetWrapper(target), out string reason))
            {
                Log.Planning.Info($"[{roleName}] Blood Oath held: can't target {target.CharacterName} ({reason})");
                return null;
            }

            remainingAP -= cost;
            Log.Planning.Info($"[{roleName}] Blood Oath -> {target.CharacterName}: {why}");
            return PlannedAction.Debuff(oath, target, $"Blood Oath on {target.CharacterName}", cost);
        }

        /// <summary>
        /// After the round's reward: the enemy she's about to hit (one she can hit now, best target first), if it's
        /// hard enough to hit that "can't be dodged or parried" is worth the wounds.
        /// </summary>
        private static BaseUnitEntity ChooseExtraOathTarget(Situation situation, AbilityData oath, out string why)
        {
            var unit = situation.Unit;
            AbilityData melee = null;
            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack?.Weapon != null && attack.IsMelee && !AbilityDatabase.IsGapCloser(attack)) { melee = attack; break; }
            }
            if (melee == null || situation.HittableEnemies == null || situation.HittableEnemies.Count == 0)
            {
                why = "kill reward already taken this round and no enemy in reach to attack";
                return null;
            }

            var candidates = new List<BaseUnitEntity>();
            if (situation.BestTarget != null && situation.HittableEnemies.Contains(situation.BestTarget)) candidates.Add(situation.BestTarget);
            foreach (var e in situation.HittableEnemies)
                if (!candidates.Contains(e)) candidates.Add(e);

            foreach (var enemy in candidates)
            {
                if (enemy == null || !enemy.IsConscious || IsAlreadySworn(situation, oath, enemy)) continue;
                var hit = CombatAPI.GetHitChance(melee, unit, enemy);
                int avoidance = hit != null ? Math.Max(hit.EstimatedDodgeChance, hit.EstimatedParryChance) : 0;
                if (avoidance >= ExtraOathMinAvoidance)
                {
                    why = $"extra oath: {enemy.CharacterName} dodges/parries {avoidance}% and the oath's attack can't be avoided";
                    return enemy;
                }
                why = $"kill reward already taken this round; {enemy.CharacterName} only dodges/parries {avoidance}%";
                return null;  // only the enemy she's about to hit is worth an extra oath
            }
            why = "kill reward already taken this round; enemies in reach are already sworn";
            return null;
        }

        /// <summary>Reachable this turn and likely to die to her best melee attack; best target score wins.</summary>
        private static BaseUnitEntity ChooseOathTarget(Situation situation, AbilityData oath, float oathCost, out string why)
        {
            var unit = situation.Unit;
            float apAfter = situation.CurrentAP - oathCost;

            float bestDamage = 0f;
            AbilityData bestAttack = null;
            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack?.Weapon == null || !attack.IsMelee || AbilityDatabase.IsGapCloser(attack)) continue;
                if (CombatAPI.GetAbilityAPCost(attack) > apAfter + 0.01f) continue;
                float dmg = CombatAPI.EstimateDamage(attack, situation.NearestEnemy ?? unit);
                if (dmg > bestDamage) { bestDamage = dmg; bestAttack = attack; }
            }
            if (bestAttack == null)
            {
                why = "no melee attack affordable after the oath";
                return null;
            }

            float bonusMP = WoundMPBonus(unit);
            float walk = situation.CurrentMP + bonusMP;
            float melee = MovementPlanner.GetUnitMeleeRange(unit);
            float jump = 0f;
            foreach (var attack in situation.AvailableAttacks)
            {
                if (IsDeathFromAbove(attack) && UsesLeft(attack) > 0)
                    jump = Math.Max(jump, CombatAPI.GetAbilityRangeInTiles(attack));
            }

            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            var scoreRole = role == AIRole.Auto ? AIRole.DPS : role;
            BaseUnitEntity best = null;
            float bestScore = float.MinValue;
            int reachable = 0;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                if (CombatAPI.IsTargetImmuneToDamage(enemy, unit)) continue;
                if (IsAlreadySworn(situation, oath, enemy)) continue;

                float dist = CombatAPI.GetDistanceInTiles(unit, enemy);
                if (dist > melee + walk + jump) continue;
                reachable++;

                // The oath's attack can't be dodged or parried, so the estimate is a fair guess at a hit.
                float dmg = CombatAPI.EstimateDamage(bestAttack, enemy);
                if (dmg < CombatAPI.GetActualHP(enemy)) continue;

                float score = TargetScorer.ScoreEnemy(enemy, situation, scoreRole);
                if (score > bestScore) { bestScore = score; best = enemy; }
            }

            string reach = $"reach {melee + walk + jump:F0} tiles (MP {situation.CurrentMP:F0}{(bonusMP > 0 ? $" +{bonusMP:F0} from wounds" : "")}{(jump > 0 ? $", jump {jump:F0}" : "")})";
            why = best != null
                ? $"{best.CharacterName} reachable and {bestAttack.Name} can kill ({reach})"
                : reachable == 0 ? $"no enemy within {reach}" : $"{reachable} enemies in reach but none killable with {bestAttack.Name}";
            return best;
        }

        /// <summary>Death From Above as an attack: land next to the most enemies (at least <see cref="StrikeMinEnemies"/>).</summary>
        private static PlannedAction PlanDeathFromAboveStrike(Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation.Unit;
            AbilityData jump = null;
            foreach (var attack in situation.AvailableAttacks)
            {
                if (!IsDeathFromAbove(attack) || UsesLeft(attack) <= 0) continue;
                if (CombatAPI.GetAbilityAPCost(attack) > remainingAP) continue;
                jump = attack;
                break;
            }
            if (jump == null) return null;

            var casterNode = unit.CurrentUnwalkableNode;
            float range = CombatAPI.GetAbilityRangeInTiles(jump);
            if (casterNode == null || range < 1f) return null;

            var oathTarget = GetOathTarget(situation);
            bool hasSelfAoE = situation.HasSelfAoE;
            bool avoidHazards = !situation.NeedsAoEEvacuation;
            var candidates = new List<(Vector3 pos, int enemies, float score)>();

            var ownNodes = unit.GetOccupiedNodes();
            foreach (var node in GridAreaHelper.GetNodesSpiralAround(casterNode, unit.SizeRect, (int)Math.Ceiling(range)))
            {
                if (node == null || !node.Walkable || ownNodes.Contains(node)) continue;
                if (node.TryGetUnit(out var occupant) && occupant != null && occupant.IsConscious) continue;
                if (!BattlefieldGrid.Instance.ValidateNode(unit, node)) continue;

                Vector3 pos = node.Vector3Position;
                if (CombatAPI.MetersToTiles(Vector3.Distance(unit.Position, pos)) > range) continue;
                if (avoidHazards && CombatAPI.IsPositionInHazardZone(pos, unit)) continue;

                int enemies = CountAdjacent(pos, situation.Enemies, unit, enemiesOnly: true);
                if (enemies < StrikeMinEnemies) continue;
                // With an oath sworn, only jump to tiles next to that enemy
                if (oathTarget != null && CombatAPI.GetDistanceInTiles(pos, oathTarget) > AdjacentTiles) continue;

                float score = enemies * 10f;
                if (hasSelfAoE) score -= CountAdjacent(pos, situation.Allies, unit, enemiesOnly: false) * 15f;
                candidates.Add((pos, enemies, score));
            }
            if (candidates.Count == 0) return null;

            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            int checks = Math.Min(candidates.Count, MaxLandingChecks);
            for (int i = 0; i < checks; i++)
            {
                if (!CombatAPI.CanUseAbilityOn(jump, new TargetWrapper(candidates[i].pos), out string reason)) continue;
                float cost = CombatAPI.GetAbilityAPCost(jump);
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Death From Above strike: landing next to {candidates[i].enemies} enemies ({UsesLeft(jump)} uses left)");
                return PlannedAction.PositionalAttack(jump, candidates[i].pos, $"Death From Above onto {candidates[i].enemies} enemies", cost);
            }
            return null;
        }

        private static int CountAdjacent(Vector3 pos, List<BaseUnitEntity> units, BaseUnitEntity self, bool enemiesOnly)
        {
            if (units == null) return 0;
            int count = 0;
            foreach (var u in units)
            {
                if (u == null || u == self || !u.IsConscious || u.LifeState.IsDead) continue;
                if (enemiesOnly && CombatAPI.IsTargetImmuneToDamage(u, self)) continue;
                if (CombatAPI.GetDistanceInTiles(pos, u) <= AdjacentTiles) count++;
            }
            return count;
        }

        #endregion

        #region Hooks for other planners

        /// <summary>Allies below this HP still get healed during an oath (same line as an emergency heal).</summary>
        private const float OathHealOverrideHP = 30f;

        /// <summary>
        /// With an oath sworn, her AP and movement go to killing that enemy: hold heals on allies who aren't in danger,
        /// so the wounds paid for the oath aren't wasted.
        /// </summary>
        public static bool HoldAllyHealForOath(Situation situation, BaseUnitEntity ally, string roleName)
        {
            if (situation?.Unit == null || ally == null || ally == situation.Unit) return false;
            var oathTarget = GetOathTarget(situation);
            if (oathTarget == null) return false;
            float hp = CombatCache.GetHPPercent(ally);
            if (hp < OathHealOverrideHP) return false;
            Log.Planning.Info($"[{roleName}] Heal on {ally.CharacterName} ({hp:F0}% HP) held: Blood Oath on {oathTarget.CharacterName} comes first");
            return true;
        }

        /// <summary>
        /// Death From Above as a plain gap closer: only with more than <see cref="GapCloserReserve"/> uses left, or to
        /// reach the oath target (a kill with a normal attack gives a use back).
        /// </summary>
        public static bool AllowJumpAsGapCloser(Situation situation, AbilityData ability, BaseUnitEntity target, string roleName)
        {
            if (!IsDeathFromAbove(ability)) return true;
            int uses = UsesLeft(ability);
            if (uses > GapCloserReserve) return true;
            var oathTarget = GetOathTarget(situation);
            if (oathTarget != null && target == oathTarget)
            {
                Log.Planning.Info($"[{roleName}] Death From Above to reach oath target {target.CharacterName} ({uses} uses left)");
                return true;
            }
            if (Main.IsDebugEnabled)
                Log.Planning.Debug($"[{roleName}] Death From Above kept back as gap closer -> {target?.CharacterName} ({uses} uses left, need more than {GapCloserReserve})");
            return false;
        }

        /// <summary>Landing tile bonus for Death From Above: more enemies hit on landing, and next to the oath target.</summary>
        public static float LandingBonus(AbilityData ability, Vector3 pos, Situation situation)
        {
            if (situation == null || !IsDeathFromAbove(ability)) return 0f;
            float bonus = CountAdjacent(pos, situation.Enemies, situation.Unit, enemiesOnly: true) * 50f;
            var oathTarget = GetOathTarget(situation);
            if (oathTarget != null && CombatAPI.GetDistanceInTiles(pos, oathTarget) <= AdjacentTiles) bonus += 30f;
            return bonus;
        }

        #endregion
    }
}
