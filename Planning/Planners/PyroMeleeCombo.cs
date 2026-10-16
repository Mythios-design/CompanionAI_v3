using System;
using System.Collections.Generic;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Pyromancer melee combo ("burning blade"), for Prefer Melee characters who have Ignite, Orchestrate Flames
    /// (Shape Flames) and a melee weapon — e.g. Heinrix. Recognised by ability ID, not by character name.
    ///
    ///   Setup (once, normally on the first turn):
    ///     Endure (if no temporary wounds) → Ignite self (only with temporary wounds) → Orchestrate Flames self.
    ///     The character then burns for the rest of the fight at half damage, and every melee hit deals full
    ///     burning damage to its target.
    ///   While active:
    ///     - Recast Endure whenever temporary wounds run out.
    ///     - Large Medikit removes burning, so it is only used on this character below 30% HP (then preferred).
    ///   Ability rules (applied when the turn's ability lists are built):
    ///     - Orchestrate Flames is reserved for the setup (never on enemies).
    ///     - Where It Hurts only when an enemy in melee reach is burning or poisoned.
    ///     - Break Through only when an enemy is in melee reach (its free Slashes need a target).
    ///     - Daring Breach (Heroic Act and Desperate Measure) only with the burning blade active and 2+ enemies in reach.
    ///   Any unit: Word of the Emperor is last-resort only (Phase 9, when nothing else used the AP).
    /// </summary>
    public static class PyroMeleeCombo
    {
        // Ability IDs (from the in-game ability dump)
        private const string IGNITE = "a2cca43669184eaa9f0da981f204e1c9";
        private const string ORCHESTRATE_FLAMES = "7720d74e51f94184bb43b97ce9c9e53f";
        private const string ENDURE = "4d2f2a839d2340388d45cf4cf66c947b";
        private const string WHERE_IT_HURTS = "cfdb72a6653c49ffa792fa99abfed128";
        private const string BREAK_THROUGH = "54f73c2523524544a8d7ffc3807056f0";
        private const string DARING_BREACH_HEROIC = "ed21642647a14ead9a09183cd5318d11";
        private const string DARING_BREACH_DESPERATE = "845a1ed417f2489489eab670b00b773a";
        private const string LARGE_MEDIKIT = "d722bfac662c40f9b2a47dc6ea70d00a";
        private const string WORD_OF_THE_EMPEROR = "6285e5ba2a804e3d9df1dd5f7fa819f2";

        // Buff IDs present while the combo is active
        private const string ORCHESTRATE_FLAMES_BUFF = "123d1d6d75394528b1955bb4c3b88103";
        private const string BURNING_BUFF = "3ddbf131cbd54087a665d4b2e026b0f2";

        private const float SETUP_MIN_HP = 50f;            // don't start burning yourself below this
        private const float LARGE_MEDIKIT_MAX_HP = 30f;    // Large Medikit (removes burning) only below this
        private const int DARING_BREACH_MIN_ENEMIES = 2;
        private const string GROUP_TAG = "BurningBladeSetup";

        // One setup attempt / one Endure refresh per unit per combat round (prevents replan loops)
        private static readonly Dictionary<string, int> _lastSetupRound = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _lastEndureRound = new Dictionary<string, int>();

        #region Queries

        private static bool Is(AbilityData ability, string guid)
            => ability?.Blueprint?.AssetGuid?.ToString() == guid;

        private static AbilityData FindAbility(BaseUnitEntity unit, string guid)
        {
            var facts = unit?.Abilities?.RawFacts;
            if (facts == null) return null;
            foreach (var fact in facts)
            {
                if (fact?.Blueprint?.AssetGuid?.ToString() == guid) return fact.Data;
            }
            return null;
        }

        private static bool HasBuff(BaseUnitEntity unit, string guid)
        {
            if (unit?.Buffs == null) return false;
            foreach (var buff in unit.Buffs)
            {
                if (buff?.Blueprint?.AssetGuid?.ToString() == guid) return true;
            }
            return false;
        }

        private static int CurrentRound() => Game.Instance?.TurnController?.CombatRound ?? 0;

        /// <summary>The burning blade is active: burning and under Orchestrate Flames.</summary>
        public static bool IsBurningBladeActive(BaseUnitEntity unit)
        {
            try { return HasBuff(unit, ORCHESTRATE_FLAMES_BUFF) && HasBuff(unit, BURNING_BUFF); }
            catch (Exception ex) { Log.Planning.Error(ex, "[PyroMelee] IsBurningBladeActive failed"); return false; }
        }

        public static int TemporaryWounds(BaseUnitEntity unit)
        {
            try { return unit?.Health?.TemporaryHitPoints ?? 0; }
            catch (Exception ex) { Log.Planning.Error(ex, "[PyroMelee] TemporaryWounds failed"); return 0; }
        }

        /// <summary>This character plays the pyromancer melee combo.</summary>
        public static bool AppliesTo(Situation situation)
        {
            if (situation?.Unit == null) return false;
            if (situation.RangePreference != RangePreference.PreferMelee || !situation.HasMeleeWeapon) return false;
            return FindAbility(situation.Unit, IGNITE) != null && FindAbility(situation.Unit, ORCHESTRATE_FLAMES) != null;
        }

        private static bool AppliesTo(BaseUnitEntity unit)
        {
            if (unit == null) return false;
            if (CombatAPI.GetRangePreference(unit) != RangePreference.PreferMelee) return false;
            return FindAbility(unit, IGNITE) != null && FindAbility(unit, ORCHESTRATE_FLAMES) != null;
        }

        private static bool IsUsableOn(AbilityData ability, BaseUnitEntity target)
        {
            if (ability == null || target == null) return false;
            if (!CombatAPI.IsAbilityAvailable(ability, out _)) return false;
            return CombatAPI.CanUseAbilityOn(ability, new TargetWrapper(target), out _);
        }

        #endregion

        #region Ability rules (analyzer hook)

        /// <summary>
        /// Called once the turn's ability lists and melee-reachable enemies are known.
        /// Removes abilities whose conditions aren't met so no planning step can pick them.
        /// </summary>
        public static void ApplyAbilityRules(Situation situation)
        {
            if (situation?.Unit == null) return;
            try
            {
                // Any unit: Word of the Emperor is last-resort only.
                var wote = RemoveFromAllLists(situation, WORD_OF_THE_EMPEROR);
                if (wote != null && !situation.LastResortBuffs.Contains(wote))
                    situation.LastResortBuffs.Add(wote);

                if (!AppliesTo(situation)) return;
                var unit = situation.Unit;
                string name = unit.CharacterName;

                // Orchestrate Flames is reserved for the self setup.
                if (RemoveFromAllLists(situation, ORCHESTRATE_FLAMES) != null && Main.IsDebugEnabled)
                    Log.Planning.Debug($"[PyroMelee] {name}: Orchestrate Flames reserved for burning blade setup");

                // Where It Hurts only with a burning/poisoned enemy in melee reach.
                bool dotTargetInReach = false;
                foreach (var enemy in situation.MeleeHittableEnemies)
                {
                    if (enemy != null && enemy.IsConscious && SpecialAbilityHandler.HasDoT(enemy)) { dotTargetInReach = true; break; }
                }
                if (!dotTargetInReach && RemoveFromAllLists(situation, WHERE_IT_HURTS) != null)
                    Log.Planning.Info($"[PyroMelee] {name}: Where It Hurts held — no burning/poisoned enemy in melee reach");

                // Break Through only with an enemy in melee reach.
                if (situation.MeleeHittableEnemies.Count == 0 && RemoveFromAllLists(situation, BREAK_THROUGH) != null)
                    Log.Planning.Info($"[PyroMelee] {name}: Break Through held — no enemy in melee reach for Slash");

                // Daring Breach only with the burning blade active and 2+ enemies in reach.
                int inReach = CountEnemiesInReach(situation);
                bool active = IsBurningBladeActive(unit);
                if (!active || inReach < DARING_BREACH_MIN_ENEMIES)
                {
                    bool removed = RemoveFromAllLists(situation, DARING_BREACH_HEROIC) != null;
                    removed |= RemoveFromAllLists(situation, DARING_BREACH_DESPERATE) != null;
                    if (removed)
                        Log.Planning.Info($"[PyroMelee] {name}: Daring Breach held (burningBlade={active}, enemiesInReach={inReach}, need {DARING_BREACH_MIN_ENEMIES})");
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, "[PyroMelee] ApplyAbilityRules failed");
            }
        }

        /// <summary>Enemies he could reach and hit this turn with a full move (Daring Breach restores movement).</summary>
        private static int CountEnemiesInReach(Situation situation)
        {
            float reachTiles = Math.Max(situation.CurrentMP, CombatAPI.GetNextTurnMP(situation.Unit)) + 2f;
            int count = 0;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || enemy.LifeState.IsDead || !enemy.IsConscious) continue;
                if (CombatAPI.GetDistanceInTiles(situation.Unit, enemy) <= reachTiles) count++;
            }
            return count;
        }

        /// <summary>Removes the ability (by ID) from every ability list on the situation. Returns it if found.</summary>
        private static AbilityData RemoveFromAllLists(Situation s, string guid)
        {
            AbilityData found = null;
            foreach (var list in new[] { s.AvailableBuffs, s.AvailableAttacks, s.AvailableHeals, s.AvailableDebuffs,
                s.AvailableSpecialAbilities, s.AvailablePositionalBuffs, s.AvailableStratagems, s.AvailableMarkers,
                s.AvailableAoEAttacks, s.PostFirstActionAbilities })
            {
                if (list == null) continue;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (Is(list[i], guid)) { found = found ?? list[i]; list.RemoveAt(i); }
                }
            }
            if (Is(s.MPBuffAbility, guid)) { found = found ?? s.MPBuffAbility; s.MPBuffAbility = null; s.MPBuffExpectedRecovery = 0f; }
            if (Is(s.RunAndGunAbility, guid)) { found = found ?? s.RunAndGunAbility; s.RunAndGunAbility = null; }
            if (Is(s.BestBuff, guid)) { found = found ?? s.BestBuff; s.BestBuff = null; }
            return found;
        }

        #endregion

        #region Setup and upkeep (planning step after emergency heal)

        /// <summary>
        /// Returns the actions for this turn's setup or Endure refresh, or null to continue with normal planning.
        /// The caller returns these as the plan; after they run, a fresh plan is made for the rest of the turn.
        /// </summary>
        public static List<PlannedAction> PlanSetupOrUpkeep(Situation situation, ref float remainingAP, string roleName)
        {
            if (!AppliesTo(situation) || situation.IsHPCritical) return null;
            try
            {
                var unit = situation.Unit;
                string id = unit.UniqueId;
                int round = CurrentRound();
                int temp = TemporaryWounds(unit);
                var endure = FindAbility(unit, ENDURE);

                if (IsBurningBladeActive(unit))
                {
                    // Upkeep: keep a temporary-wounds buffer while burning.
                    if (temp > 0 || endure == null) return null;
                    if (_lastEndureRound.TryGetValue(id, out int er) && er == round) return null;
                    float endureCost = CombatAPI.GetAbilityAPCost(endure);
                    if (endureCost > remainingAP || !IsUsableOn(endure, unit)) return null;
                    _lastEndureRound[id] = round;
                    remainingAP -= endureCost;
                    Log.Planning.Info($"[{roleName}] Burning blade: refreshing Endure (no temporary wounds)");
                    return new List<PlannedAction> { PlannedAction.Buff(endure, unit, "Burning blade: Endure refresh", endureCost) };
                }

                // Setup
                if (situation.HPPercent < SETUP_MIN_HP) return null;
                if (_lastSetupRound.TryGetValue(id, out int sr) && sr == round) return null;

                var ignite = FindAbility(unit, IGNITE);
                var orchestrate = FindAbility(unit, ORCHESTRATE_FLAMES);
                if (ignite == null || orchestrate == null) return null;
                if (!IsUsableOn(ignite, unit) || !CombatAPI.IsAbilityAvailable(orchestrate, out _)) return null;

                bool needEndure = temp <= 0;
                if (needEndure && (endure == null || !IsUsableOn(endure, unit))) return null;  // no buffer → no combo

                float igniteCost = CombatAPI.GetAbilityAPCost(ignite);
                float orchestrateCost = CombatAPI.GetAbilityAPCost(orchestrate);
                float endureCostSetup = needEndure ? CombatAPI.GetAbilityAPCost(endure) : 0f;
                float total = endureCostSetup + igniteCost + orchestrateCost;
                if (total > remainingAP) return null;

                _lastSetupRound[id] = round;
                remainingAP -= total;

                var actions = new List<PlannedAction>();
                if (needEndure) actions.Add(Grouped(PlannedAction.Buff(endure, unit, "Burning blade: Endure buffer", endureCostSetup)));
                actions.Add(Grouped(PlannedAction.Buff(ignite, unit, "Burning blade: Ignite self", igniteCost)));
                actions.Add(Grouped(PlannedAction.Buff(orchestrate, unit, "Burning blade: Orchestrate Flames self", orchestrateCost)));
                Log.Planning.Info($"[{roleName}] Burning blade setup: {(needEndure ? "Endure → " : "")}Ignite self → Orchestrate Flames self (AP {total:F1}, tempWounds={temp})");
                return actions;
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, "[PyroMelee] PlanSetupOrUpkeep failed");
                return null;
            }
        }

        private static PlannedAction Grouped(PlannedAction action)
        {
            action.GroupTag = GROUP_TAG;
            action.FailurePolicy = GroupFailurePolicy.SkipRemainingInGroup;
            return action;
        }

        /// <summary>Clear per-combat tracking (combat start/end).</summary>
        public static void Reset()
        {
            _lastSetupRound.Clear();
            _lastEndureRound.Clear();
        }

        #endregion

        #region Heals and execution safety

        /// <summary>Large Medikit removes burning: only on a burning-blade character below 30% HP.</summary>
        public static bool AllowHeal(AbilityData heal, BaseUnitEntity target)
        {
            if (!Is(heal, LARGE_MEDIKIT) || target == null) return true;
            // Burning-blade characters keep the Large Medikit for below 30% HP, whether or not the blade is lit.
            if (!IsBurningBladeActive(target) && FindAbility(target, ORCHESTRATE_FLAMES) == null) return true;
            return CombatAPI.GetHPPercent(target) < LARGE_MEDIKIT_MAX_HP;
        }

        /// <summary>Score bonus so the emergency heal on a burning-blade character picks Large Medikit (puts the fire out).</summary>
        public static float HealPreferenceBonus(AbilityData heal, BaseUnitEntity target)
        {
            if (!Is(heal, LARGE_MEDIKIT) || target == null) return 0f;
            return IsBurningBladeActive(target) && CombatAPI.GetHPPercent(target) < LARGE_MEDIKIT_MAX_HP ? 1000f : 0f;
        }

        /// <summary>Final check just before casting. Returns false (with a reason) to block the cast.</summary>
        public static bool AllowAtExecution(AbilityData ability, BaseUnitEntity caster, BaseUnitEntity target, out string reason)
        {
            reason = null;
            try
            {
                if (ability == null) return true;
                if (Is(ability, LARGE_MEDIKIT) && !AllowHeal(ability, target))
                {
                    reason = "Large Medikit would put out the burning blade (HP not below 30%)";
                    return false;
                }
                if (!AppliesTo(caster)) return true;
                if (Is(ability, ORCHESTRATE_FLAMES) && target != caster)
                {
                    reason = "Orchestrate Flames is reserved for the burning blade setup";
                    return false;
                }
                if (Is(ability, IGNITE) && target == caster && TemporaryWounds(caster) <= 0)
                {
                    reason = "no temporary wounds — not igniting self";
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Engine.Error(ex, "[PyroMelee] AllowAtExecution failed");
            }
            return true;
        }

        #endregion
    }
}
