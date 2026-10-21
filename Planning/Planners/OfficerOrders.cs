using System.Collections.Generic;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.Data;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Officer: Bring It Down! comes before Voice of Command.
    ///  - Pick the Bring It Down! ally first (TurnGrantPolicy: an engaged melee ally, or a ranged ally who can
    ///    shoot from where they stand), carry unit first.
    ///  - AP for both: Voice of Command on that ally, then Bring It Down! on them. A kill on the bonus turn then
    ///    gives extra momentum, and Voice of Command lets Bring It Down! reach the ally from any distance
    ///    (normally 4 tiles).
    ///  - AP for one: Bring It Down! only. Voice of Command gets whatever AP is left, on anyone.
    ///  - No ally worth it: nothing here, the normal ally-buff order applies.
    /// PlanAllyBuff returns one action per call, so the pair is planned over two calls: the second call sees
    /// Voice of Command planned on the ally and adds Bring It Down!. Both share a group, so if Voice of
    /// Command fails, Bring It Down! is skipped and the turn replans.
    /// </summary>
    public static class OfficerOrders
    {
        private const string VOICE_OF_COMMAND = "9c78e44bf8ff44a9afff8370c673c9ad";
        private const string GroupPrefix = "BringItDown_";
        // Entry in the plan's buff/target set: Voice of Command was planned on this ally to pair with Bring It Down!
        private const string PairMarker = "bringitdownpair:";

        public static PlannedAction Plan(Situation situation, ref float remainingAP, List<BaseUnitEntity> prioritizedTargets,
            HashSet<string> plannedTurnGrantTargetIds, HashSet<string> plannedBuffTargetPairs,
            Dictionary<string, int> plannedAbilityUseCounts, string roleName)
        {
            AbilityData bringItDown = null, voice = null;
            foreach (var b in situation.AvailableBuffs)
            {
                if (b == null) continue;
                if (bringItDown == null && AbilityDatabase.IsBringItDown(b)) bringItDown = b;
                else if (voice == null && b.Blueprint?.AssetGuid?.ToString() == VOICE_OF_COMMAND) voice = b;
            }
            if (bringItDown == null) return null;

            string bidGuid = bringItDown.Blueprint.AssetGuid.ToString();
            float bidCost = CombatAPI.GetAbilityAPCost(bringItDown);
            float voiceCost = voice != null ? CombatAPI.GetAbilityAPCost(voice) : 0f;

            // Second half of a pair: Voice of Command planned by this planner (marked below) on an ally who still
            // needs Bring It Down!. A Voice of Command the normal ally-buff order chose doesn't count. The grant
            // check runs again, so the ally must still be worth a bonus turn.
            if (voice != null && plannedBuffTargetPairs != null)
            {
                foreach (var ally in prioritizedTargets)
                {
                    string id = IdOf(ally);
                    if (!plannedBuffTargetPairs.Contains(PairMarker + id)) continue;
                    if (plannedTurnGrantTargetIds != null && plannedTurnGrantTargetIds.Contains(id)) continue;
                    if (bidCost > remainingAP + 0.01f) return null;
                    if (!TurnGrantPolicy.IsWorthGranting(bringItDown, ally, out string pairWhy))
                    {
                        Log.Planning.Info($"[{roleName}] Skip {bringItDown.Name} -> {ally.CharacterName} after Voice of Command: {pairWhy}");
                        continue;
                    }
                    return PlanGrant(bringItDown, ally, bidCost, ref remainingAP, plannedTurnGrantTargetIds, GroupPrefix + id,
                        $"[{roleName}] Bring It Down! -> {ally.CharacterName} after Voice of Command", roleName);
                }
            }

            if (bidCost > remainingAP + 0.01f) return null;

            bool voiceFree = voice != null && !VoicePlanned(plannedAbilityUseCounts) && voiceCost + bidCost <= remainingAP + 0.01f;

            foreach (var ally in OrderWithCarryFirst(prioritizedTargets))
            {
                if (ally == null || ally.LifeState.IsDead) continue;
                string id = IdOf(ally);
                if (plannedTurnGrantTargetIds != null && plannedTurnGrantTargetIds.Contains(id)) continue;
                if (plannedBuffTargetPairs != null && plannedBuffTargetPairs.Contains($"{bidGuid}:{id}")) continue;
                if (!TurnGrantPolicy.IsWorthGranting(bringItDown, ally, out string why))
                {
                    Log.Planning.Info($"[{roleName}] Skip {bringItDown.Name} -> {ally.CharacterName}: {why}");
                    continue;
                }

                bool inReach = CombatAPI.CanUseAbilityOn(bringItDown, new TargetWrapper(ally), out string bidWhy);
                bool voiceOnAlly = voiceFree && CombatAPI.CanUseAbilityOn(voice, new TargetWrapper(ally), out _);

                if (voiceOnAlly)
                {
                    remainingAP -= voiceCost;
                    var action = PlannedAction.Buff(voice, ally, $"Voice of Command on {ally.CharacterName} before Bring It Down!", voiceCost);
                    action.GroupTag = GroupPrefix + id;
                    action.FailurePolicy = GroupFailurePolicy.SkipRemainingInGroup;
                    plannedBuffTargetPairs?.Add(PairMarker + id);
                    Log.Planning.Info($"[{roleName}] Voice of Command -> {ally.CharacterName}, then Bring It Down!" +
                        (inReach ? "" : $" (out of reach without it: {bidWhy})"));
                    return action;
                }
                if (!inReach)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{roleName}] Skip {bringItDown.Name} -> {ally.CharacterName}: {bidWhy}");
                    continue;
                }

                string note = voice == null ? "" : voiceFree ? " (Voice of Command can't target them)" : " (no AP for Voice of Command as well)";
                return PlanGrant(bringItDown, ally, bidCost, ref remainingAP, plannedTurnGrantTargetIds, null,
                    $"[{roleName}] Bring It Down! -> {ally.CharacterName}{note}", roleName);
            }
            return null;
        }

        /// <summary>The action is the Voice of Command that opens a pair with Bring It Down! (the second half follows in its group).</summary>
        public static bool OpensPair(PlannedAction action)
            => action?.GroupTag != null && action.GroupTag.StartsWith(GroupPrefix)
               && action.Ability?.Blueprint?.AssetGuid?.ToString() == VOICE_OF_COMMAND;

        /// <summary>The normal ally-buff order leaves Bring It Down! to this planner.</summary>
        public static bool Handles(AbilityData buff) => AbilityDatabase.IsBringItDown(buff);

        private static PlannedAction PlanGrant(AbilityData bringItDown, BaseUnitEntity ally, float cost, ref float remainingAP,
            HashSet<string> plannedTurnGrantTargetIds, string group, string logLine, string roleName)
        {
            remainingAP -= cost;
            plannedTurnGrantTargetIds?.Add(IdOf(ally));
            var action = PlannedAction.Buff(bringItDown, ally, $"Bring It Down! on {ally.CharacterName}", cost);
            if (group != null)
            {
                action.GroupTag = group;
                action.FailurePolicy = GroupFailurePolicy.SkipRemainingInGroup;
            }
            Log.Planning.Info(logLine);
            return action;
        }

        private static bool VoicePlanned(Dictionary<string, int> plannedAbilityUseCounts)
            => plannedAbilityUseCounts != null && plannedAbilityUseCounts.TryGetValue(VOICE_OF_COMMAND, out int n) && n > 0;

        private static IEnumerable<BaseUnitEntity> OrderWithCarryFirst(List<BaseUnitEntity> targets)
        {
            string carryId = TeamBlackboard.Instance?.HeroicActPriorityUnitId;
            BaseUnitEntity carry = null;
            if (carryId != null)
                foreach (var t in targets)
                    if (t?.UniqueId == carryId) { carry = t; break; }
            if (carry != null) yield return carry;
            foreach (var t in targets)
                if (t != carry) yield return t;
        }

        private static string IdOf(BaseUnitEntity unit) => unit.UniqueId ?? unit.CharacterName ?? "unknown";
    }
}
