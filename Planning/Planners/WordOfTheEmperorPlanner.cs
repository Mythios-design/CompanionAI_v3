using System;
using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Sanctic Word of the Emperor (1 AP, point within 12 cells, 5-cell circle, allies): stacking Resolve for every ally
    /// in the circle until the end of combat. It is a psychic power and raises the Veil by 1 each cast, so it is a
    /// deliberate group buff, never filler: held out of every list (AbilityPolicy) and cast with leftover AP only on the
    /// point covering the most allies, when that is at least <see cref="MinAllies"/> (the caster counts) and the Veil is
    /// in the safe range.
    /// </summary>
    public static class WordOfTheEmperorPlanner
    {
        private const string WORD_OF_THE_EMPEROR = "6285e5ba2a804e3d9df1dd5f7fa819f2";
        private const int MinAllies = 3;
        private const float CastRange = 12f;
        private const float Radius = 5f;

        public static bool IsWordOfTheEmperor(AbilityData ability) => ability?.Blueprint?.AssetGuid?.ToString() == WORD_OF_THE_EMPEROR;

        public static void Append(List<PlannedAction> actions, Situation situation, ref float remainingAP, string roleName)
        {
            var unit = situation?.Unit;
            var ability = situation?.HeldWordOfTheEmperor;
            if (unit == null || actions == null || ability == null) return;
            try
            {
                float cost = CombatAPI.GetAbilityAPCost(ability);
                if (cost > remainingAP + 0.01f) return;

                var safety = CombatAPI.EvaluatePsychicSafety(ability);
                if (safety != CombatAPI.PsychicSafetyLevel.Safe)
                {
                    Log.Planning.Info($"[{roleName}] Word of the Emperor held: Veil {CombatAPI.GetVeilThickness()} ({safety})");
                    return;
                }

                var allies = new List<BaseUnitEntity> { unit };
                if (situation.Allies != null)
                    foreach (var a in situation.Allies)
                        if (a != null && a != unit && a.IsConscious && !a.LifeState.IsDead && !a.IsPet) allies.Add(a);

                // Candidate centres: each ally's position within cast range
                Vector3 best = default;
                int bestCount = 0;
                foreach (var centre in allies)
                {
                    if (CombatAPI.GetDistanceInTiles(unit, centre) > CastRange) continue;
                    int count = 0;
                    foreach (var a in allies)
                        if (CombatAPI.GetDistanceInTiles(centre.Position, a) <= Radius) count++;
                    if (count <= bestCount) continue;
                    if (!CombatAPI.CanUseAbilityOn(ability, new TargetWrapper(centre.Position), out _)) continue;
                    bestCount = count;
                    best = centre.Position;
                }
                if (bestCount < MinAllies)
                {
                    Log.Planning.Info($"[{roleName}] Word of the Emperor held: best point covers {bestCount} allies (needs {MinAllies})");
                    return;
                }

                int insertAt = actions.Count;
                for (int i = 0; i < actions.Count; i++)
                    if (actions[i].Ability != null && Data.AbilityDatabase.IsTurnEnding(actions[i].Ability)) { insertAt = i; break; }
                if (insertAt > 0 && actions[insertAt - 1].Type == ActionType.EndTurn) insertAt--;
                actions.Insert(insertAt, PlannedAction.PositionalAttack(ability, best,
                    $"Word of the Emperor on {bestCount} allies", cost, requiresEnemyOccupancy: false));
                remainingAP -= cost;
                Log.Planning.Info($"[{roleName}] Word of the Emperor: {bestCount} allies in the circle, Veil {CombatAPI.GetVeilThickness()}");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[WordOfTheEmperor] planning failed for {unit.CharacterName}");
            }
        }
    }
}
