using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.Pathfinding;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.UnitLogic.Abilities.Blueprints;
using Kingmaker.UnitLogic.Abilities.Components.Patterns;
using Kingmaker.UnitLogic.Mechanics.Actions;
using Kingmaker.Utility;
using UnityEngine;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;
using CompanionAI_v3.Settings;

namespace CompanionAI_v3.Planning.Planners
{
    /// <summary>
    /// Places the Grand Strategist's Frontline, Backline and Rear zones together.
    ///
    /// Each zone is scored by who benefits from standing in it:
    ///   Frontline: armour for allies, extra damage taken by enemies inside → melee allies and the enemies they fight.
    ///   Rear: ranged damage and crits (damaging psychic powers count) → ranged attackers.
    ///   Backline: cover, dodge and dodge reduction → supports and other ranged characters.
    /// Footprints come from the game's own zone patterns, so coverage and the no-overlap rule match the game.
    /// All three are chosen as one combination with no overlaps; an existing zone is only moved when the
    /// new spot is clearly better.
    /// </summary>
    public static class StrategistZonePlanner
    {
        private const int CandidatesPerZone = 8;
        private const int CandidateSpreadTiles = 2;      // try target tiles up to this far around each ally
        private const float MoveThreshold = 1.0f;        // a zone moves only for at least this much more value

        public enum ZoneKind { Frontline, Backline, Rear }

        private enum AllyKind { Melee, Ranged, Support }

        private sealed class ZoneOption
        {
            public ZoneKind Kind;
            public AbilityData Ability;
            public CustomGridNodeBase TargetNode;   // null = keep the existing zone
            public HashSet<CustomGridNodeBase> Nodes;
            public float Value;
            public string Who;
        }

        #region Public API

        /// <summary>
        /// Plans zone placements/moves for this turn. Zones cost 0 AP. Returns the actions in an order the game
        /// will accept (no step overlaps a zone that is still on the map) and marks the abilities as used.
        /// </summary>
        public static List<PlannedAction> PlanZones(Situation situation, HashSet<string> usedGuids, string roleName)
        {
            var result = new List<PlannedAction>();
            if (situation?.Unit == null || situation.AvailablePositionalBuffs == null) return result;

            try
            {
                var unit = situation.Unit;
                var casterNode = unit.Position.GetNearestNodeXZ() as CustomGridNodeBase;
                if (casterNode == null) return result;

                var zoneAbilities = new Dictionary<ZoneKind, AbilityData>();
                foreach (var ability in situation.AvailablePositionalBuffs)
                {
                    var kind = GetZoneKind(ability);
                    if (kind.HasValue && !zoneAbilities.ContainsKey(kind.Value)) zoneAbilities[kind.Value] = ability;
                }
                Log.Planning.Info($"[{roleName}] StrategistZonePlanner: zone abilities [{string.Join(", ", zoneAbilities.Keys)}], " +
                    $"positional buffs available={situation.AvailablePositionalBuffs.Count}");
                if (zoneAbilities.Count == 0) return result;

                var existing = GetExistingZones(unit);
                var allies = GetAllies(situation);
                var kinds = ClassifyAllies(allies);

                // Options per zone: keep the existing one (if any) and the best new spots.
                var optionsByKind = new List<List<ZoneOption>>();
                foreach (var pair in zoneAbilities)
                {
                    var options = new List<ZoneOption>();
                    if (existing.TryGetValue(pair.Key, out var entity))
                    {
                        var keepNodes = new HashSet<CustomGridNodeBase>();
                        foreach (var n in entity.CoveredNodes) if (n is CustomGridNodeBase cn) keepNodes.Add(cn);
                        options.Add(new ZoneOption
                        {
                            Kind = pair.Key, Ability = pair.Value, TargetNode = null, Nodes = keepNodes,
                            Value = ScoreFootprint(pair.Key, keepNodes, allies, kinds, situation.Enemies, out string keepWho),
                            Who = keepWho
                        });
                    }

                    options.AddRange(FindBestPlacements(pair.Key, pair.Value, casterNode, allies, kinds, situation.Enemies,
                        hasExisting: existing.ContainsKey(pair.Key)));
                    if (!existing.ContainsKey(pair.Key))
                        options.Add(null);  // a zone not on the map yet may stay unplaced
                    optionsByKind.Add(options);
                }

                var best = FindBestCombination(optionsByKind);
                if (best == null) return result;

                // Only zones that are new or moved produce actions; order them so each cast is legal.
                var moves = best.Where(o => o != null && o.TargetNode != null).ToList();
                var placed = new Dictionary<ZoneKind, HashSet<CustomGridNodeBase>>();
                foreach (var pair in existing)
                {
                    var set = new HashSet<CustomGridNodeBase>();
                    foreach (var n in pair.Value.CoveredNodes) if (n is CustomGridNodeBase cn) set.Add(cn);
                    placed[pair.Key] = set;
                }

                bool progress = true;
                while (moves.Count > 0 && progress)
                {
                    progress = false;
                    for (int i = 0; i < moves.Count; i++)
                    {
                        var move = moves[i];
                        bool blocked = placed.Any(p => p.Key != move.Kind && p.Value.Overlaps(move.Nodes));
                        if (blocked) continue;

                        var point = move.TargetNode.Vector3Position;
                        if (!CombatAPI.CanUseAbilityOn(move.Ability, new TargetWrapper(point), out string reason))
                        {
                            Log.Planning.Info($"[{roleName}] Zone {move.Kind}: target rejected ({reason})");
                            moves.RemoveAt(i); i--; continue;
                        }

                        string verb = existing.ContainsKey(move.Kind) ? "move" : "place";
                        Log.Planning.Info($"[{roleName}] Zone {move.Kind}: {verb} at ({point.x:F1},{point.z:F1}), value={move.Value:F1} [{move.Who}]");
                        result.Add(PlannedAction.PositionalBuff(move.Ability, point, $"{move.Kind} zone ({move.Who})",
                            CombatAPI.GetAbilityAPCost(move.Ability)));
                        usedGuids?.Add(move.Ability.Blueprint?.AssetGuid?.ToString() ?? move.Ability.Name);
                        placed[move.Kind] = move.Nodes;
                        moves.RemoveAt(i); i--;
                        progress = true;
                    }
                }
                foreach (var stuck in moves)
                    Log.Planning.Info($"[{roleName}] Zone {stuck.Kind}: skipped this turn (would overlap a zone that can't move first)");

                // Zones we keep or can't improve are still "handled" so the generic positional loop leaves them alone.
                foreach (var pair in zoneAbilities)
                    usedGuids?.Add(pair.Value.Blueprint?.AssetGuid?.ToString() ?? pair.Value.Name);

                foreach (var keep in best.Where(o => o != null && o.TargetNode == null))
                    Log.Planning.Info($"[{roleName}] Zone {keep.Kind}: keeping current spot, value={keep.Value:F1} [{keep.Who}]");
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, $"[{roleName}] StrategistZonePlanner failed");
            }
            return result;
        }

        /// <summary>True if this ability is a Frontline/Backline/Rear zone that this planner handles.</summary>
        public static bool IsZoneAbility(AbilityData ability) => GetZoneKind(ability).HasValue;

        /// <summary>
        /// Value of each existing zone for Combat Locus: (kind, value, a point inside it).
        /// </summary>
        public static List<(ZoneKind kind, float value, Vector3 position)> GetExistingZoneValues(Situation situation)
        {
            var list = new List<(ZoneKind, float, Vector3)>();
            if (situation?.Unit == null) return list;
            try
            {
                var allies = GetAllies(situation);
                var kinds = ClassifyAllies(allies);
                foreach (var pair in GetExistingZones(situation.Unit))
                {
                    var nodes = new HashSet<CustomGridNodeBase>();
                    foreach (var n in pair.Value.CoveredNodes) if (n is CustomGridNodeBase cn) nodes.Add(cn);
                    float value = ScoreFootprint(pair.Key, nodes, allies, kinds, situation.Enemies, out _);
                    list.Add((pair.Key, value, pair.Value.Position));
                }
            }
            catch (Exception ex)
            {
                Log.Planning.Error(ex, "[StrategistZonePlanner] GetExistingZoneValues failed");
            }
            return list;
        }

        #endregion

        #region Scoring

        private static float AllyWeight(ZoneKind zone, AllyKind ally)
        {
            switch (zone)
            {
                case ZoneKind.Frontline: return ally == AllyKind.Melee ? 3f : 1f;
                case ZoneKind.Rear: return ally == AllyKind.Ranged ? 3f : ally == AllyKind.Support ? 1f : 0f;
                case ZoneKind.Backline: return ally == AllyKind.Support ? 2f : ally == AllyKind.Ranged ? 1.5f : 0.5f;
            }
            return 0f;
        }

        private static float ScoreFootprint(ZoneKind zone, HashSet<CustomGridNodeBase> nodes,
            List<BaseUnitEntity> allies, Dictionary<BaseUnitEntity, AllyKind> kinds, List<BaseUnitEntity> enemies, out string who)
        {
            float value = 0f;
            var names = new List<string>();
            if (nodes == null || nodes.Count == 0) { who = ""; return 0f; }

            foreach (var ally in allies)
            {
                if (!IsInside(ally, nodes)) continue;
                float w = AllyWeight(zone, kinds[ally]);
                if (w <= 0f) continue;
                value += w;
                names.Add(ally.CharacterName);
            }

            // Frontline also makes enemies inside take more damage; worth most next to our melee fighters.
            if (zone == ZoneKind.Frontline && enemies != null)
            {
                int enemyCount = 0;
                foreach (var enemy in enemies)
                {
                    if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                    if (!IsInside(enemy, nodes)) continue;
                    bool engaged = allies.Any(a => kinds[a] == AllyKind.Melee && CombatAPI.GetDistanceInTiles(a, enemy) <= 2f);
                    value += engaged ? 2f : 1f;
                    enemyCount++;
                }
                if (enemyCount > 0) names.Add($"{enemyCount} enemies");
            }

            who = string.Join(", ", names);
            return value;
        }

        private static bool IsInside(BaseUnitEntity unit, HashSet<CustomGridNodeBase> nodes)
        {
            try
            {
                foreach (var occ in unit.GetOccupiedNodes())
                    if (occ is CustomGridNodeBase cn && nodes.Contains(cn)) return true;
            }
            catch
            {
                // fall back to the unit's centre tile
            }
            return unit.Position.GetNearestNodeXZ() is CustomGridNodeBase c && nodes.Contains(c);
        }

        #endregion

        #region Placement search

        private static List<ZoneOption> FindBestPlacements(ZoneKind kind, AbilityData ability, CustomGridNodeBase casterNode,
            List<BaseUnitEntity> allies, Dictionary<BaseUnitEntity, AllyKind> kinds, List<BaseUnitEntity> enemies, bool hasExisting)
        {
            var areaBp = GetAreaEffect(ability);
            var found = new List<ZoneOption>();
            if (areaBp == null) return found;

            var tried = new HashSet<CustomGridNodeBase>();
            var anchors = new List<Vector3>();
            foreach (var a in allies) if (AllyWeight(kind, kinds[a]) > 0f) anchors.Add(a.Position);
            if (kind == ZoneKind.Frontline && enemies != null)
                foreach (var e in enemies)
                    if (e != null && e.IsConscious && allies.Any(a => kinds[a] == AllyKind.Melee && CombatAPI.GetDistanceInTiles(a, e) <= 4f))
                        anchors.Add(e.Position);

            int triedCount = 0, emptyPatterns = 0, zeroValue = 0;
            string firstError = null;
            foreach (var anchor in anchors)
            {
                for (int dx = -CandidateSpreadTiles; dx <= CandidateSpreadTiles; dx++)
                {
                    for (int dz = -CandidateSpreadTiles; dz <= CandidateSpreadTiles; dz++)
                    {
                        var pos = anchor + new Vector3(dx * CombatAPI.GridCellSize, 0f, dz * CombatAPI.GridCellSize);
                        var node = pos.GetNearestNodeXZ() as CustomGridNodeBase;
                        if (node == null || !tried.Add(node)) continue;
                        triedCount++;

                        var nodes = GetFootprint(ability, areaBp, casterNode, node, ref firstError);
                        if (nodes == null || nodes.Count == 0) { emptyPatterns++; continue; }

                        float value = ScoreFootprint(kind, nodes, allies, kinds, enemies, out string who);
                        if (value <= 0f) { zeroValue++; continue; }
                        if (hasExisting) value -= MoveThreshold;  // moving must beat keeping by a margin
                        found.Add(new ZoneOption { Kind = kind, Ability = ability, TargetNode = node, Nodes = nodes, Value = value, Who = who });
                    }
                }
            }

            Log.Planning.Info($"[StrategistZonePlanner] {kind}: anchors={anchors.Count}, tried={triedCount}, emptyFootprint={emptyPatterns}, " +
                $"nobodyInside={zeroValue}, usable={found.Count}" + (firstError != null ? $", error={firstError}" : ""));

            return found.OrderByDescending(o => o.Value).Take(CandidatesPerZone).ToList();
        }

        /// <summary>
        /// Tiles a zone would cover if aimed at targetNode. Uses the ability's own pattern (what the game shows
        /// when targeting), falling back to the zone blueprint's oriented pattern.
        /// </summary>
        private static HashSet<CustomGridNodeBase> GetFootprint(AbilityData ability, BlueprintAbilityAreaEffect areaBp,
            CustomGridNodeBase casterNode, CustomGridNodeBase targetNode, ref string firstError)
        {
            var nodes = new HashSet<CustomGridNodeBase>();
            try
            {
                var pattern = CombatAPI.GetAffectedNodes(ability, targetNode.Vector3Position, casterNode.Vector3Position);
                if (!pattern.IsEmpty)
                    foreach (var n in pattern.Nodes) if (n is CustomGridNodeBase cn) nodes.Add(cn);
            }
            catch (Exception ex) { firstError = firstError ?? $"ability pattern: {ex.GetType().Name}: {ex.Message}"; }

            if (nodes.Count > 0) return nodes;

            try
            {
                var pattern = areaBp.GetOrientedPattern(casterNode, targetNode);
                if (!pattern.IsEmpty)
                    foreach (var n in pattern.Nodes) if (n is CustomGridNodeBase cn) nodes.Add(cn);
            }
            catch (Exception ex) { firstError = firstError ?? $"zone pattern: {ex.GetType().Name}: {ex.Message}"; }

            return nodes;
        }

        private static List<ZoneOption> FindBestCombination(List<List<ZoneOption>> optionsByKind)
        {
            List<ZoneOption> best = null;
            float bestValue = float.MinValue;
            var current = new List<ZoneOption>();

            void Search(int index, float value)
            {
                if (index == optionsByKind.Count)
                {
                    if (value > bestValue) { bestValue = value; best = new List<ZoneOption>(current); }
                    return;
                }
                foreach (var option in optionsByKind[index])
                {
                    if (option != null && current.Any(c => c != null && c.Nodes.Overlaps(option.Nodes))) continue;
                    current.Add(option);
                    Search(index + 1, value + (option?.Value ?? 0f));
                    current.RemoveAt(current.Count - 1);
                }
            }

            Search(0, 0f);
            return best;
        }

        #endregion

        #region Helpers

        private static ZoneKind? GetZoneKind(AbilityData ability)
        {
            var type = GetAreaEffect(ability)?.TacticsAreaEffectType.ToString();
            switch (type)
            {
                case "Frontline": return ZoneKind.Frontline;
                case "Backline": return ZoneKind.Backline;
                case "Rear": return ZoneKind.Rear;
                default: return null;
            }
        }

        private static BlueprintAbilityAreaEffect GetAreaEffect(AbilityData ability)
        {
            try
            {
                var spawn = ability?.Blueprint?.ElementsArray?.OfType<ContextActionSpawnAreaEffect>().FirstOrDefault();
                var area = spawn?.AreaEffect;
                return area != null && area.IsStrategistAbility ? area : null;
            }
            catch
            {
                return null;  // not a zone ability
            }
        }

        private static Dictionary<ZoneKind, AreaEffectEntity> GetExistingZones(BaseUnitEntity caster)
        {
            var zones = new Dictionary<ZoneKind, AreaEffectEntity>();
            var areaEffects = Game.Instance?.State?.AreaEffects;
            if (areaEffects == null) return zones;
            foreach (var area in areaEffects)
            {
                var bp = area?.Blueprint;
                if (bp == null || !bp.IsStrategistAbility) continue;
                if (area.Context?.MaybeCaster != caster) continue;
                switch (bp.TacticsAreaEffectType.ToString())
                {
                    case "Frontline": zones[ZoneKind.Frontline] = area; break;
                    case "Backline": zones[ZoneKind.Backline] = area; break;
                    case "Rear": zones[ZoneKind.Rear] = area; break;
                }
            }
            return zones;
        }

        private static List<BaseUnitEntity> GetAllies(Situation situation)
        {
            var allies = (situation.CombatantAllies ?? new List<BaseUnitEntity>())
                .Where(a => a != null && a.IsConscious && !a.LifeState.IsDead).ToList();
            if (!allies.Contains(situation.Unit)) allies.Add(situation.Unit);
            return allies;
        }

        /// <summary>
        /// Melee: Tank role or prefers melee. Support: Support role. Everyone else attacks at range (weapons or
        /// damaging psychic powers), which is what Rear rewards.
        /// </summary>
        private static Dictionary<BaseUnitEntity, AllyKind> ClassifyAllies(List<BaseUnitEntity> allies)
        {
            var kinds = new Dictionary<BaseUnitEntity, AllyKind>();
            foreach (var ally in allies)
            {
                var settings = ModSettings.Instance?.GetOrCreateSettings(ally.UniqueId, ally.CharacterName);
                var role = settings?.Role ?? AIRole.Auto;
                if (role == AIRole.Auto) role = RoleDetector.DetectOptimalRole(ally);
                var range = settings?.RangePreference ?? RangePreference.Adaptive;

                if (role == AIRole.Tank || range == RangePreference.PreferMelee) kinds[ally] = AllyKind.Melee;
                else if (role == AIRole.Support) kinds[ally] = AllyKind.Support;
                else kinds[ally] = AllyKind.Ranged;
            }
            return kinds;
        }

        #endregion
    }
}
