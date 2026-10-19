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
    /// Bladedancer rules, from the game's blueprints:
    ///  - Blood Oath (a buff on the enemy, no dodge or parry against her) is removed by her next attack roll on that
    ///    enemy, kill or not; Blade Dance is the exception (its kill pays, a hit that doesn't kill removes the oath
    ///    when the dance ends). A kill on a sworn enemy pays: heal, and on the round's first such kill +1 AP and an
    ///    extra weapon attack (the game marks that with a 1-round buff on her). Who is sworn is read from the game's
    ///    buff, never assumed. The oath only goes on an enemy she can walk to this turn (counting the MP that
    ///    spending wounds gives) and can kill with her next attack.
    ///  - Death From Above's landing makes an attack roll on every enemy next to it for a flat Weapon Skill bonus +
    ///    Agility bonus, so it spends any oath it touches: no landing next to a sworn enemy it won't kill. With a jump
    ///    needed to get somewhere, she jumps first and swears after landing.
    ///  - Death From Above is a free attack when the landing is next to enough enemies. As a plain gap closer it is
    ///    used on a target in range when a melee attack can still be used on it after landing.
    /// The MP bonus from wound spending (a common Bladedancer talent) is learned in the fight: the first time she
    /// spends wounds, the MP gained is recorded and used for later oath decisions.
    /// </summary>
    public static class BladedancerTactics
    {
        private const string BLOOD_OATH = "590c990c1d684fd09ae883754d28a8ac";
        private const string BLOOD_OATH_BUFF = "91028e859ede4a698744a9ee148ee778";    // ReaperBloodOath_Buff, on the enemy
        private const string OATH_REWARD_BUFF = "44f1d979142f4d13b82c9777e2f93d6e";   // ReaperBloodOathCaster_Buff: round's reward taken

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
        // Attacks she has made this combat, and the count when each enemy was sworn: an oath with no attack since
        // is kept even if its buff isn't visible yet
        private static readonly Dictionary<string, int> _attackCount = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _attacksAtOath = new Dictionary<string, int>();
        private static readonly Dictionary<string, string> _lastSworn = new Dictionary<string, string>();
        private static readonly Dictionary<string, float> _woundMPBonus = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _mpBeforeWoundSpend = new Dictionary<string, float>();

        public static void Reset()
        {
            _oathTargets.Clear();
            _swornThisRound.Clear();
            _attackCount.Clear();
            _attacksAtOath.Clear();
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

        /// <summary>The game's own mark that her round's oath kill reward (+1 AP, extra attack) has been paid.</summary>
        public static bool OathRewardTakenThisRound(BaseUnitEntity unit) => HasBuff(unit, OATH_REWARD_BUFF, null);

        /// <summary>The enemy carries her Blood Oath (the game's buff, cast by her).</summary>
        public static bool HasOathFrom(BaseUnitEntity enemy, BaseUnitEntity caster) => HasBuff(enemy, BLOOD_OATH_BUFF, caster);

        private static bool HasBuff(BaseUnitEntity unit, string guid, BaseUnitEntity caster)
        {
            var buffs = unit?.Buffs?.Enumerable;
            if (buffs == null) return false;
            foreach (var b in buffs)
            {
                if (b?.Blueprint?.AssetGuid?.ToString() != guid) continue;
                if (caster == null || b.Context?.MaybeCaster == caster) return true;
            }
            return false;
        }

        /// <summary>Death From Above's landing damage on each adjacent enemy: Weapon Skill bonus + Agility bonus.</summary>
        public static int LandingDamage(BaseUnitEntity unit)
        {
            var stats = unit?.Stats;
            if (stats == null) return 0;
            return (stats.GetAttributeOptional(Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerWeaponSkill)?.Bonus ?? 0)
                + (stats.GetAttributeOptional(Kingmaker.EntitySystem.Stats.Base.StatType.WarhammerAgility)?.Bonus ?? 0);
        }

        /// <summary>
        /// A sworn enemy whose oath a Death From Above landing at pos would spend: next to the landing and not killed
        /// by its flat damage. Null when the landing is safe for her oaths.
        /// </summary>
        public static BaseUnitEntity LandingSpendsOath(Situation situation, Vector3 pos)
        {
            if (situation?.Unit == null) return null;
            int damage = LandingDamage(situation.Unit);
            foreach (var sworn in GetOathTargets(situation))
                if (CombatAPI.GetDistanceInTiles(pos, sworn) <= AdjacentTiles && CombatAPI.GetActualHP(sworn) > damage) return sworn;
            return null;
        }

        public static bool IsBloodOath(AbilityData ability) => Guid(ability) == BLOOD_OATH;

        public static bool IsDeathFromAbove(AbilityData ability)
        {
            string guid = Guid(ability);
            return guid != null && DeathFromAboveGuids.Contains(guid);
        }

        private static string Guid(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString();

        /// <summary>Uses of the ability left. The game reports -1 when it has no limit.</summary>
        public static int UsesLeft(AbilityData ability)
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

                if (action.Type == ActionType.Attack || BladedancerPositioning.IsBladeDance(ability))
                {
                    _attackCount.TryGetValue(unit.UniqueId, out int n);
                    _attackCount[unit.UniqueId] = n + 1;
                }
                var target = action.Target?.Entity as BaseUnitEntity;
                if (IsBloodOath(ability) && target != null)
                {
                    _attackCount.TryGetValue(unit.UniqueId, out int attacks);
                    _attacksAtOath[unit.UniqueId + target.UniqueId] = attacks;
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

        /// <summary>Tiles she can walk to this turn (MP plus the MP spending wounds gives), where she stands included.</summary>
        private static List<CustomGridNodeBase> WalkTiles(Situation situation)
        {
            var list = new List<CustomGridNodeBase>();
            var unit = situation.Unit;
            if (unit.CurrentUnwalkableNode is CustomGridNodeBase here) list.Add(here);
            float mp = situation.CurrentMP + WoundMPBonus(unit);
            if (mp <= 0f) return list;
            var tiles = MovementAPI.FindAllReachableTilesWithThreatsSync(unit, mp);
            if (tiles == null) return list;
            foreach (var cell in tiles.Values)
                if (cell.IsCanStand && cell.Node is CustomGridNodeBase node) list.Add(node);
            return list;
        }

        /// <summary>
        /// She can attack the enemy this turn on foot: a tile she can walk to is in melee reach of it. A jump doesn't
        /// count, since its landing would spend the oath; she jumps first and swears after landing.
        /// Uses the real walking paths, not a straight line.
        /// </summary>
        private static bool CanGetAt(Situation situation, BaseUnitEntity enemy, List<CustomGridNodeBase> walkTiles)
        {
            float melee = MovementPlanner.GetUnitMeleeRange(situation.Unit) + 0.5f;
            foreach (var node in walkTiles)
                if (CombatAPI.GetDistanceInTiles(node.Vector3Position, enemy) <= melee) return true;
            return false;
        }

        /// <summary>How far she can walk to attack this turn: melee reach + MP (and the wound MP bonus).</summary>
        private static float OathReach(Situation situation, out string text)
        {
            var unit = situation.Unit;
            float bonusMP = WoundMPBonus(unit);
            float walk = situation.CurrentMP + bonusMP;
            float reach = MovementPlanner.GetUnitMeleeRange(unit) + walk;
            text = $"walking reach {reach:F0} tiles (MP {situation.CurrentMP:F0}{(bonusMP > 0 ? $" +{bonusMP:F0} from wounds" : "")})";
            return reach;
        }

        /// <summary>
        /// Living enemies carrying her Blood Oath (the game's buff), in the order sworn. An oath sworn with no attack of
        /// hers since counts even before its buff shows. Oaths that are gone are dropped and logged: spent by a hit
        /// that didn't kill, or the enemy died (with the round's reward if the game marked it on her).
        /// </summary>
        public static List<BaseUnitEntity> GetOathTargets(Situation situation)
        {
            var result = new List<BaseUnitEntity>();
            var unit = situation?.Unit;
            if (unit == null || situation.Enemies == null) return result;
            if (!_oathTargets.TryGetValue(unit.UniqueId, out var ids))
                _oathTargets[unit.UniqueId] = ids = new List<string>();
            _attackCount.TryGetValue(unit.UniqueId, out int attacks);

            for (int i = ids.Count - 1; i >= 0; i--)
            {
                BaseUnitEntity enemy = null;
                foreach (var e in situation.Enemies)
                    if (e != null && e.UniqueId == ids[i]) { enemy = e; break; }
                bool alive = enemy != null && enemy.IsConscious && !enemy.LifeState.IsDead;
                if (alive && HasOathFrom(enemy, unit)) continue;
                string key = unit.UniqueId + ids[i];
                if (alive && _attacksAtOath.TryGetValue(key, out int atOath) && atOath == attacks) continue;  // buff not visible yet

                if (alive)
                    Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: Blood Oath on {enemy.CharacterName} spent by a hit that didn't kill");
                else
                    Log.Planning.Info($"[Bladedancer] {unit.CharacterName}: sworn enemy down" +
                        (OathRewardTakenThisRound(unit) ? ", round's oath reward taken" : ""));
                _attacksAtOath.Remove(key);
                ids.RemoveAt(i);
            }
            // Oaths the game shows that weren't recorded (e.g. sworn by the player before the AI took over)
            foreach (var e in situation.Enemies)
                if (e != null && e.IsConscious && !e.LifeState.IsDead && !ids.Contains(e.UniqueId) && HasOathFrom(e, unit))
                    ids.Insert(0, e.UniqueId);
            foreach (var id in ids)
                foreach (var enemy in situation.Enemies)
                    if (enemy != null && enemy.UniqueId == id) { result.Add(enemy); break; }
            return result;
        }

        /// <summary>Already sworn on this round, or still carrying her oath: Blood Oath goes on each enemy once.</summary>
        private static bool IsAlreadySworn(Situation situation, BaseUnitEntity enemy)
            => SwornThisRound(situation.Unit.UniqueId).Contains(enemy.UniqueId) || HasOathFrom(enemy, situation.Unit);

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
        ///  - Only on an enemy her next attack can kill on its best damage roll: the lowest-HP one (see <see cref="ChooseOathTarget"/>).
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
        /// The lowest-HP enemy that one of her affordable melee attacks can kill on its best damage roll (ties: best
        /// target score). A good roll is enough because the oath's attack can't be dodged or parried. It must
        /// be hittable from where she stands when she's engaged (walking away would draw attacks of opportunity and the
        /// "attack before moving" rule keeps her on the adjacent enemy); otherwise anywhere within her reach this turn.
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
            List<CustomGridNodeBase> walkTiles = null;  // tiles she can walk to this turn, built on first need
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
                if (IsAlreadySworn(situation, enemy)) continue;
                if (engaged) { if (!situation.HittableEnemies.Contains(enemy)) continue; }
                else
                {
                    if (CombatAPI.GetDistanceInTiles(unit, enemy) > reach) continue;
                    if (walkTiles == null) walkTiles = WalkTiles(situation);
                    if (!CanGetAt(situation, enemy, walkTiles)) continue;
                }
                inReach++;

                float hp = CombatAPI.GetActualHP(enemy);
                if (hp > bestHP + 0.01f) continue;
                AbilityData killer = null;
                foreach (var attack in melee)
                    if (CombatAPI.GetDamagePrediction(attack, enemy).MaxDamage >= hp) { killer = attack; break; }
                if (killer == null) continue;

                float score = TargetScorer.ScoreEnemy(enemy, situation, scoreRole);
                if (hp < bestHP - 0.01f || score > bestScore) { bestHP = hp; bestScore = score; best = enemy; killWith = killer.Name; }
            }

            why = best != null
                ? $"{best.CharacterName} has the least HP ({bestHP:F0}) of the enemies {killWith} can kill on a top roll, {where}"
                : inReach == 0 ? $"no enemy {where}" : $"{inReach} enemies {where}, none her next attack can kill even on a top roll";
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
                // The landing's attack roll would spend an oath it doesn't kill
                if (LandingSpendsOath(situation, pos) != null) continue;

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

            // Landing next to a sworn enemy makes an attack roll on it: the oath is spent unless the landing kills it
            if (target != null && HasOathFrom(target, situation.Unit) && CombatAPI.GetActualHP(target) > LandingDamage(situation.Unit))
            {
                Log.Planning.Info($"[{roleName}] Death From Above kept back -> {target.CharacterName}: the landing would spend the Blood Oath " +
                    $"(landing {LandingDamage(situation.Unit)} damage, {CombatAPI.GetActualHP(target):F0} HP)");
                return false;
            }

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

        /// <summary>
        /// A sworn enemy next to her that a single-target melee attack (not Blade Dance, a jump or a gap closer) can hit
        /// now within the AP left, or null. That attack goes before Blade Dance.
        /// </summary>
        public static BaseUnitEntity SwornEnemyToAttackFirst(Situation situation, AbilityData bladeDance, float remainingAP, out string attackName)
        {
            attackName = null;
            var unit = situation?.Unit;
            if (unit == null || situation.AvailableAttacks == null) return null;
            foreach (var sworn in GetOathTargets(situation))
            {
                if (CombatAPI.GetDistanceInTiles(unit, sworn) > AdjacentTiles) continue;
                foreach (var a in situation.AvailableAttacks)
                {
                    if (a == null || a == bladeDance || a.Weapon == null || !a.IsMelee) continue;
                    if (BladedancerPositioning.IsBladeDance(a) || IsDeathFromAbove(a) || AbilityDatabase.IsGapCloser(a)) continue;
                    if (CombatAPI.GetEffectiveAPCost(a) > remainingAP + 0.01f) continue;
                    if (!CombatAPI.CanUseAbilityOn(a, new TargetWrapper(sworn), out _)) continue;
                    attackName = a.Name;
                    return sworn;
                }
            }
            return null;
        }

        /// <summary>
        /// Landing tile bonus for Death From Above: more enemies hit on landing, a sworn enemy the landing kills (that
        /// pays the oath), and away from allies when Blade Dance will follow (the landing itself never hits allies).
        /// </summary>
        public static float LandingBonus(AbilityData ability, Vector3 pos, Situation situation)
        {
            if (situation == null || !IsDeathFromAbove(ability)) return 0f;
            float bonus = CountAdjacent(pos, situation.Enemies, situation.Unit, enemiesOnly: true) * 50f;
            if (situation.HasSelfAoE)
                bonus -= CountAdjacent(pos, situation.Allies, situation.Unit, enemiesOnly: false) * BladedancerPositioning.AllyPenalty;
            int damage = LandingDamage(situation.Unit);
            foreach (var sworn in GetOathTargets(situation))
                if (CombatAPI.GetDistanceInTiles(pos, sworn) <= AdjacentTiles && CombatAPI.GetActualHP(sworn) <= damage) bonus += 30f;
            return bonus;
        }

        #endregion
    }
}
