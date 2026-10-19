using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Buffs.Blueprints;
using Kingmaker.Utility;
using Kingmaker.View.Covers;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Operative's Tactical Knowledge (1 AP, point within 10 cells, 5-cell circle): removes 1 exploit from each enemy
    /// in the circle, or all of them when only one enemy is in it. Allies gain +(removed)% armour and, at 4 or more
    /// removed, the Operative +(2 x removed)% damage, until the end of combat. A new cast replaces the old buff.
    ///
    /// Held out of every list by AbilityPolicy and placed here as the last step of the plan, after the Operative's own
    /// setup and shot (Expose Weakness has already spent the shot target's exploits by then):
    ///  - only if it removes more exploits than the buff already active from this unit this combat;
    ///  - 4 or more removed: always worth it (may take the AP of a leftover Analyse Enemies);
    ///  - 1 to 3: only with AP that would otherwise go unused.
    /// Exploits are the stacking debuff Analyse Enemies puts on its target (read from its blueprint, not by name).
    /// </summary>
    public static class TacticalKnowledgePlanner
    {
        private const string TACTICAL_KNOWLEDGE = "2a417cde46674ae0a5da8dcfd9ec621d";
        private static readonly HashSet<string> AnalyseGuids = new HashSet<string>
        {
            "502b9195c84c42c5b180f3f63ed7955c", "18f968c992344aa08d873bab8ae6a7af",
            "aa56d3dd1b744673ba16672a1ea1cfb1", "50dd16833a7a4ffcaab972fe3fca3980",
        };
        private const float CastRange = 10f;
        private const float Radius = 5f;
        private const int DamageBonusAt = 4;

        // Exploits removed by the buff now active, per caster, for this combat
        private static readonly Dictionary<string, int> _active = new Dictionary<string, int>();
        // Exploits a planned cast expects to remove, recorded as active once it runs
        private static readonly ConditionalWeakTable<PlannedAction, StrongBox<int>> _planned = new ConditionalWeakTable<PlannedAction, StrongBox<int>>();
        private static BlueprintBuff _exploitBuff;
        private static bool _exploitBuffLogged;

        public static bool IsTacticalKnowledge(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString() == TACTICAL_KNOWLEDGE;

        public static void Reset() => _active.Clear();

        /// <summary>Called from TurnState.RecordAction for a successful action.</summary>
        public static void OnActionExecuted(BaseUnitEntity unit, PlannedAction action)
        {
            if (unit == null || action == null || !IsTacticalKnowledge(action.Ability)) return;
            if (_planned.TryGetValue(action, out var box))
            {
                _active[unit.UniqueId] = box.Value;
                Log.Planning.Info($"[TacticalKnowledge] {unit.CharacterName}: buff now from {box.Value} exploits removed");
            }
        }

        public static void Append(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var tk = situation?.HeldTacticalKnowledge;
            if (tk == null || actions == null) return;
            try
            {
                var exploit = ExploitBuff(situation);
                if (exploit == null) return;

                var unit = situation.Unit;
                Vector3 from = unit.Position;
                foreach (var a in actions)
                    if (a.Type == ActionType.Move && a.MoveDestination.HasValue) from = a.MoveDestination.Value;
                var fromNode = from.GetNearestNodeXZ() as CustomGridNodeBase;
                if (fromNode == null) return;

                // Expose Weakness planned this turn spends its target's exploits first
                var spent = new HashSet<BaseUnitEntity>();
                foreach (var a in actions)
                    if (a.Ability != null && Data.AbilityDatabase.IsExposeWeakness(a.Ability) && a.Target?.Entity is BaseUnitEntity t) spent.Add(t);

                int best = 0;
                Vector3 bestPoint = default;
                string bestWho = null;
                foreach (var centre in situation.Enemies)
                {
                    if (centre == null || !centre.IsConscious) continue;
                    var point = centre.Position;
                    if (CombatAPI.GetDistanceInTiles(from, point) > CastRange) continue;
                    var node = point.GetNearestNodeXZ() as CustomGridNodeBase;
                    if (node == null || !tk.CanTargetFromNode(fromNode, node, new TargetWrapper(point),
                            out int _, out LosCalculations.CoverType _, out AbilityData.UnavailabilityReasonType? _))
                        continue;

                    int inArea = 0, withStacks = 0, loneStacks = 0;
                    string who = null;
                    foreach (var e in situation.Enemies)
                    {
                        if (e == null || !e.IsConscious || CombatAPI.GetDistanceInTiles(point, e) > Radius) continue;
                        inArea++;
                        int stacks = spent.Contains(e) ? 0 : Stacks(e, exploit);
                        if (stacks > 0) { withStacks++; who = e.CharacterName; }
                        loneStacks = stacks;
                    }
                    int removed = inArea == 1 ? loneStacks : withStacks;
                    if (removed <= best) continue;
                    best = removed;
                    bestPoint = point;
                    bestWho = inArea == 1 ? $"all {removed} of {who}'s exploits" : $"1 exploit each from {removed} enemies";
                }

                _active.TryGetValue(unit.UniqueId, out int active);
                if (best <= active)
                {
                    if (best > 0) Log.Planning.Info($"[{roleName}] Tactical Knowledge held: best cast removes {best}, the active buff is from {active}");
                    return;
                }

                float cost = CombatAPI.GetAbilityAPCost(tk);
                if (cost > remainingAP + 0.01f && best >= DamageBonusAt)
                {
                    // Worth more than a leftover Analyse Enemies on the next target: take its AP
                    int idx = actions.FindLastIndex(a => a.Type == ActionType.Debuff && a.Ability?.Blueprint != null
                        && AnalyseGuids.Contains(a.Ability.Blueprint.AssetGuid.ToString()) && a.Reason != null && a.Reason.StartsWith("Exploits on next target"));
                    if (idx >= 0)
                    {
                        remainingAP += actions[idx].APCost;
                        actions.RemoveAt(idx);
                    }
                }
                if (cost > remainingAP + 0.01f)
                {
                    Log.Planning.Info($"[{roleName}] Tactical Knowledge held: removes {best} but needs {cost:F0} AP, {remainingAP:F0} left");
                    return;
                }

                var action = PlannedAction.PositionalAttack(tk, bestPoint,
                    $"Tactical Knowledge: {bestWho}", cost, requiresEnemyOccupancy: false);
                _planned.Add(action, new StrongBox<int>(best));
                int insertAt = actions.Count;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Ability != null && Data.AbilityDatabase.IsTurnEnding(actions[i].Ability)) { insertAt = i; break; }
                if (insertAt > 0 && actions[insertAt - 1].Type == ActionType.EndTurn) insertAt--;
                actions.Insert(insertAt, action);
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Tactical Knowledge: {bestWho} (active buff was {active}" +
                    (best >= DamageBonusAt ? ", damage bonus on)" : ", armour only)"));
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[TacticalKnowledge] planning failed for {situation.Unit?.CharacterName}");
            }
        }

        /// <summary>The stacking debuff Analyse Enemies puts on its target, from any Analyse the unit or an ally has.</summary>
        private static BlueprintBuff ExploitBuff(Situation situation)
        {
            if (_exploitBuff != null) return _exploitBuff;
            var units = new List<BaseUnitEntity> { situation.Unit };
            if (situation.Allies != null) units.AddRange(situation.Allies);
            foreach (var u in units)
            {
                var raw = u?.Abilities?.RawFacts;
                if (raw == null) continue;
                foreach (var fact in raw)
                {
                    var bp = fact?.Data?.Blueprint;
                    if (bp == null || !AnalyseGuids.Contains(bp.AssetGuid?.ToString() ?? "")) continue;
                    var buffs = AbilityBuffMap.Get(bp)?.TargetBuffs;
                    if (buffs == null) continue;
                    foreach (var b in buffs)
                    {
                        if (b == null || (b.Stacking != StackingType.Rank && b.Stacking != StackingType.Stack)) continue;
                        _exploitBuff = b;
                        Log.Planning.Info($"[TacticalKnowledge] exploit debuff: {b.name} ({b.AssetGuid}, stacking {b.Stacking})");
                        return b;
                    }
                }
            }
            if (!_exploitBuffLogged)
            {
                _exploitBuffLogged = true;
                Log.Planning.Info("[TacticalKnowledge] exploit debuff not found from Analyse Enemies - Tactical Knowledge not planned");
            }
            return null;
        }

        private static int Stacks(BaseUnitEntity enemy, BlueprintBuff exploit)
        {
            int count = 0, rank = 0;
            foreach (var buff in enemy.Buffs.Enumerable)
            {
                if (buff?.Blueprint != exploit) continue;
                count++;
                rank = Math.Max(rank, buff.Rank);
            }
            return exploit.Stacking == StackingType.Rank ? rank : count;
        }
    }
}
