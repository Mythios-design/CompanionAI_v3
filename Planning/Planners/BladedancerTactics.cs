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
    ///    used on a target in range when a melee attack can still be used on it after landing.
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

        /// <summary>Enemies next to the landing tile before a jump is worth it as an attack.</summary>
        public const int StrikeMinEnemies = 2;

        private const float AdjacentTiles = 1.5f;
        private const int MaxLandingChecks = 12;

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

        /// <summary>
        /// Her main oath target: the most recently sworn living enemy she can reach this turn, else the most recently
        /// sworn living one, or null. A new oath only goes out when the older ones are out of reach, so the newest is
        /// the one she's working on.
        /// </summary>
        public static BaseUnitEntity GetOathTarget(Situation situation)
        {
            var living = GetOathTargets(situation);
            if (living.Count == 0) return null;
            float reach = OathReach(situation, out _);
            for (int i = living.Count - 1; i >= 0; i--)
                if (CombatAPI.GetDistanceInTiles(situation.Unit, living[i]) <= reach) return living[i];
            return living[living.Count - 1];
        }

        /// <summary>How far she can get to attack this turn: melee reach + MP (and the wound MP bonus) + a jump.</summary>
        private static float OathReach(Situation situation, out string text)
        {
            var unit = situation.Unit;
            float bonusMP = WoundMPBonus(unit);
            float walk = situation.CurrentMP + bonusMP;
            float melee = MovementPlanner.GetUnitMeleeRange(unit);
            float jump = 0f;
            if (situation.AvailableAttacks != null)
                foreach (var attack in situation.AvailableAttacks)
                    if (IsDeathFromAbove(attack) && UsesLeft(attack) > 0)
                        jump = Math.Max(jump, CombatAPI.GetAbilityRangeInTiles(attack));
            float reach = melee + walk + jump;
            text = $"reach {reach:F0} tiles (MP {situation.CurrentMP:F0}{(bonusMP > 0 ? $" +{bonusMP:F0} from wounds" : "")}{(jump > 0 ? $", jump {jump:F0}" : "")})";
            return reach;
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
        /// Blood Oath pays off only when her next attack on the sworn enemy kills it (heal, +1 AP and an extra attack,
        /// the last two once per round); otherwise the wounds are spent for nothing. So:
        ///  - At most one oath a turn, and none while a sworn enemy she can reach is still alive.
        ///  - Only on an enemy her next attack should kill: the lowest-HP one (see <see cref="ChooseOathTarget"/>).
        ///    Her attacks then go at that enemy (oath target priority in the analyzer and the attack planner).
        /// No HP limit, since the cost can't kill her and the kill heals her.
        /// </summary>
        private static PlannedAction PlanBloodOath(Situation situation, ref float remainingAP, string roleName)
        {
            AbilityData oath = null;
            foreach (var marker in situation.AvailableMarkers)
                if (IsBloodOath(marker)) { oath = marker; break; }
            if (oath == null) return null;

            float cost = CombatAPI.GetAbilityAPCost(oath);
            if (cost > remainingAP) return null;

            if (SwornThisRound(situation.Unit.UniqueId).Count > 0)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{roleName}] Blood Oath held: already sworn this turn");
                return null;
            }
            float reachNow = OathReach(situation, out _);
            foreach (var sworn in GetOathTargets(situation))
                if (CombatAPI.GetDistanceInTiles(situation.Unit, sworn) <= reachNow) return null;  // that one still needs killing

            var target = ChooseOathTarget(situation, oath, remainingAP - cost, out string why);
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
        /// The lowest-HP enemy that one of her affordable melee attacks should kill (ties: best target score). It must
        /// be hittable from where she stands when she's engaged (walking away would draw attacks of opportunity and the
        /// "attack before moving" rule keeps her on the adjacent enemy); otherwise anywhere within her reach this turn.
        /// The oath's attack can't be dodged or parried, so the damage estimate is a fair guess at a hit.
        /// </summary>
        private static BaseUnitEntity ChooseOathTarget(Situation situation, AbilityData oath, float apAfterOath, out string why)
        {
            var unit = situation.Unit;
            var melee = new List<AbilityData>();
            foreach (var attack in situation.AvailableAttacks)
            {
                if (attack?.Weapon == null || !attack.IsMelee || AbilityDatabase.IsGapCloser(attack)) continue;
                if (CombatAPI.GetEffectiveAPCost(attack) > apAfterOath + 0.01f) continue;
                melee.Add(attack);
            }
            if (melee.Count == 0)
            {
                why = "no melee attack affordable after the oath";
                return null;
            }

            bool engaged = CombatAPI.IsInThreateningArea(unit);
            float reach = OathReach(situation, out string reachText);
            string where = engaged ? "hittable from here (engaged)" : $"within {reachText}";
            var role = situation.CharacterSettings?.Role ?? AIRole.Auto;
            var scoreRole = role == AIRole.Auto ? AIRole.DPS : role;

            BaseUnitEntity best = null;
            string killWith = null;
            float bestHP = float.MaxValue, bestScore = float.MinValue;
            int inReach = 0;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                if (CombatAPI.IsTargetImmuneToDamage(enemy, unit)) continue;
                if (IsAlreadySworn(situation, oath, enemy)) continue;
                if (engaged ? !situation.HittableEnemies.Contains(enemy) : CombatAPI.GetDistanceInTiles(unit, enemy) > reach) continue;
                inReach++;

                float hp = CombatAPI.GetActualHP(enemy);
                if (hp > bestHP + 0.01f) continue;
                AbilityData killer = null;
                foreach (var attack in melee)
                    if (CombatAPI.EstimateDamage(attack, enemy) >= hp) { killer = attack; break; }
                if (killer == null) continue;

                float score = TargetScorer.ScoreEnemy(enemy, situation, scoreRole);
                if (hp < bestHP - 0.01f || score > bestScore) { bestHP = hp; bestScore = score; best = enemy; killWith = killer.Name; }
            }

            why = best != null
                ? $"{best.CharacterName} has the least HP ({bestHP:F0}) of the enemies {killWith} should kill, {where}"
                : inReach == 0 ? $"no enemy {where}" : $"{inReach} enemies {where}, none her next attack should kill";
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
        /// Death From Above as a plain gap closer: only when a melee attack can still be used on the target after
        /// landing (available, and affordable with the AP left after the jump). Whether the target is in reach is the
        /// gap closer planner's own range and landing check.
        /// </summary>
        public static bool AllowJumpAsGapCloser(Situation situation, AbilityData ability, BaseUnitEntity target, float remainingAP,
            float remainingMP, string roleName)
        {
            if (!IsDeathFromAbove(ability)) return true;

            // Walk first: a target within walking reach is reached on foot, keeping the charge. The jump stays allowed
            // when an enemy is already next to her, since walking away would provoke attacks of opportunity.
            if (target != null && !CombatAPI.IsInThreateningArea(situation.Unit))
            {
                float dist = CombatAPI.GetDistanceInTiles(situation.Unit, target);
                float reach = MovementPlanner.GetUnitMeleeRange(situation.Unit);
                if (dist <= remainingMP + reach)
                {
                    if (Main.IsDebugEnabled)
                        Log.Planning.Debug($"[{roleName}] Death From Above kept back -> {target.CharacterName}: reachable on foot ({dist:F1} <= MP {remainingMP:F1} + reach {reach:F1})");
                    return false;
                }
            }
            float apAfter = remainingAP - CombatAPI.GetAbilityAPCost(ability);
            var followUp = FindFollowUpMelee(situation, ability, apAfter);
            if (followUp != null)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{roleName}] Death From Above to reach {target?.CharacterName}: {followUp.Name} " +
                    $"({CombatAPI.GetEffectiveAPCost(followUp):F0} AP) usable after landing ({UsesLeft(ability)} uses left)");
                return true;
            }
            if (Main.IsDebugEnabled)
                Log.Planning.Debug($"[{roleName}] Death From Above kept back as gap closer -> {target?.CharacterName}: no melee attack usable after landing (AP after jump {apAfter:F1})");
            return false;
        }

        /// <summary>The cheapest available melee attack (not a jump) affordable with this AP, else null.</summary>
        private static AbilityData FindFollowUpMelee(Situation situation, AbilityData jump, float apAfter)
        {
            AbilityData best = null;
            float bestCost = float.MaxValue;
            if (situation?.AvailableAttacks == null) return null;
            foreach (var a in situation.AvailableAttacks)
            {
                if (a == null || a == jump || !a.IsMelee || a.Blueprint?.CanTargetEnemies != true) continue;
                if (IsDeathFromAbove(a) || AbilityDatabase.IsGapCloser(a)) continue;
                float cost = CombatAPI.GetEffectiveAPCost(a);
                if (cost > apAfter + 0.01f || cost >= bestCost) continue;
                best = a;
                bestCost = cost;
            }
            return best;
        }

        /// <summary>Landing tile bonus for Death From Above: more enemies hit on landing, next to the oath target, away from allies.</summary>
        public static float LandingBonus(AbilityData ability, Vector3 pos, Situation situation)
        {
            if (situation == null || !IsDeathFromAbove(ability)) return 0f;
            float bonus = CountAdjacent(pos, situation.Enemies, situation.Unit, enemiesOnly: true) * 50f
                - CountAdjacent(pos, situation.Allies, situation.Unit, enemiesOnly: false) * BladedancerPositioning.AllyPenalty;
            var oathTarget = GetOathTarget(situation);
            if (oathTarget != null && CombatAPI.GetDistanceInTiles(pos, oathTarget) <= AdjacentTiles) bonus += 30f;
            return bonus;
        }

        #endregion
    }
}
