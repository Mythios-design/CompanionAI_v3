using System.Collections.Generic;
using Kingmaker.AI;
using Kingmaker.EntitySystem;
using Kingmaker.EntitySystem.Entities;
using Kingmaker.SpaceCombat.StarshipLogic.Parts;
using Kingmaker.UnitLogic.Abilities;
using Kingmaker.Utility;
using Warhammer.SpaceCombat.Blueprints;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Planners
{
    public enum ShipPhase { PlanStart, AfterShot, TurnEnd }

    public sealed class ShipCast
    {
        public AbilityData Ability;
        public TargetWrapper Target;
        /// <summary>The game's ship AI throws its plan away after this cast and plans again from where the ship is.</summary>
        public bool BreaksPlan;
    }

    /// <summary>
    /// The player ship's self-cast post abilities and ultimates. The game's ship AI only casts abilities that can
    /// hit an enemy from its path, so these would never be used. ShipPostPatch asks for one cast at a time at three
    /// points of the ship's turn:
    ///  - Plan start (every plan, replans included): defensive or offensive ultimate, Boost Lance, Arc Augury,
    ///    New Heading. The path is then planned with the effect already on.
    ///  - After a macrobattery volley or torpedo launch: Rapid Reload, then a replan so the reloaded weapon fires.
    ///  - Turn end, once the final position and heading are known: Restart Shields, Reinforce Shields.
    /// Game rules: micro-abilities cost no AP and start a 1 to 3 round cooldown, one per post per round (shared
    /// group cooldown); all ultimates share one group with a 999-round cooldown, so the ship gets one per combat.
    /// IsAvailable covers cooldowns, resources and restrictions. Each ability is tried at most once per turn.
    /// Enemy-targeted post abilities and ultimates (Scan Weakness, Shield Pulse, Boarding Team...) stay with the
    /// game's AI, steered by ShipAIPatch.ShipTargetValue.
    /// </summary>
    public static class ShipPostPlanner
    {
        // Ultimates (one per combat)
        private const string CORP_CHANT = "d991f8b9a1b846d19cad4b8c2ee23afa";       // UA_ShieldGen_CorpChant_Ability
        private const string EVASION_TACTICS = "5cb239f28d5d43879fe561ba0cd1a23a";  // UA_Auspex_EvasionTactics_Ability
        private const string MASS_TORPEDO = "b978862a3a3f4d66bbea3e1bc280e6b8";     // UA_Munitorium_MassTorpedo_Ability
        private const string OVERDRIVE = "8a6bacf1ece147f18ea27498906374cc";        // UA_Engine_Overdrive_Ability
        private const string BOARDING_TEAM = "738ca17f7a52449c9f9fc16bb59319d1";
        private const string EMPYREAN_STORM = "f9b7259637d54f04bfa36753ce00a2b8";

        // Post abilities
        private const string BOOST_LANCE = "c460357afce84f3e9f7f43e3e945210e";        // EnginseerPrime_BoostLance (once per combat)
        private const string ARC_AUGURY = "b8a32c74248c469b86fff4cc1bdf126d";         // +2 range to the next attack
        private const string ARC_AUGURY_2 = "9579a7f796e24b299aabeb928ecf03b2";       // +3
        private const string NEW_HEADING = "0c82c0c7db764a69bc0708ed02de8b6c";
        private const string NEW_HEADING_2 = "509ad92b7cea4dd98765ad7ffe2cdc7b";
        private const string RAPID_RELOAD = "9c115131f98f4273af06890aa9d558d2";       // macrobatteries
        private const string RAPID_RELOAD_2 = "386999d9601d4f2badad1186728bd772";     // macrobatteries, lances -25% to hit this turn
        private const string RAPID_RELOAD_TORPEDO = "fcaad00a14fd4e9ca23222ac7daeb930";
        private const string RESTART_SHIELDS = "bd61b300bcd84e06a80c8cf3b699c78b";
        private const string RESTART_SHIELDS_2 = "713c4a59fdec45fcad3d75b462ddf33d";
        private const string REINFORCE_SHIELDS = "79529b9e5433460cb639e133fb00447b";  // variants per sector
        private const string REINFORCE_SHIELDS_2 = "835ef5d52ea34d9e92c5879603a4f505";

        private static readonly Dictionary<string, StarshipSectorShieldsType> ReinforceSector = new Dictionary<string, StarshipSectorShieldsType>
        {
            ["938997091665422bbda5849ffd3958c6"] = StarshipSectorShieldsType.Fore,
            ["82e24934bd024f3d99facdef1c6ce914"] = StarshipSectorShieldsType.Fore,
            ["f9bc0d9c304341d99ce37a3dc1266f8e"] = StarshipSectorShieldsType.Port,
            ["a44c8b58161c409fb407443819c02b08"] = StarshipSectorShieldsType.Port,
            ["83cf20ebce06422ab9c4cc71d02a57cf"] = StarshipSectorShieldsType.Starboard,
            ["c041d935b41a406ab7615c429a5fca9c"] = StarshipSectorShieldsType.Starboard,
            ["c5f4049d12744a1187d08fb935cc1a97"] = StarshipSectorShieldsType.Aft,
            ["8ffc3916fe744e4cbee9139708c6e50e"] = StarshipSectorShieldsType.Aft,
        };

        private static readonly string[] OtherUltimates = { CORP_CHANT, EVASION_TACTICS, MASS_TORPEDO, BOARDING_TEAM, EMPYREAN_STORM };

        private sealed class TurnMemory
        {
            public readonly HashSet<string> Tried = new HashSet<string>();
            public readonly HashSet<string> Logged = new HashSet<string>();
            public StarshipWeaponType? PendingReload;
        }

        private static readonly Dictionary<string, TurnMemory> _turns = new Dictionary<string, TurnMemory>();

        public static void OnTurnStart(BaseUnitEntity ship)
        {
            if (ship?.UniqueId != null) _turns[ship.UniqueId] = new TurnMemory();
        }

        /// <summary>A macrobattery volley or torpedo launch was just ordered; Rapid Reload is considered once it lands.</summary>
        public static void NoteShot(BaseUnitEntity ship, AbilityData shot)
        {
            var type = ShipCombatInfo.WeaponType(shot);
            if (type != StarshipWeaponType.Macrobatteries && type != StarshipWeaponType.TorpedoTubes) return;
            Memory(ship).PendingReload = type;
        }

        public static bool HasPendingShot(BaseUnitEntity ship) => ship?.UniqueId != null
            && _turns.TryGetValue(ship.UniqueId, out var m) && m.PendingReload != null;

        public static ShipCast Next(ShipPhase phase, StarshipEntity ship, DecisionContext context)
        {
            if (ship == null) return null;
            var memory = Memory(ship);
            var enemies = ShipCombatInfo.CapitalEnemies(context);
            if (enemies.Count == 0) return null;

            switch (phase)
            {
                case ShipPhase.PlanStart: return PlanStart(ship, memory, enemies);
                case ShipPhase.AfterShot: return AfterShot(ship, memory, enemies);
                default: return TurnEnd(ship, memory, enemies);
            }
        }

        private static ShipCast PlanStart(StarshipEntity ship, TurnMemory memory, List<StarshipEntity> enemies)
        {
            float mp = ShipCombatInfo.MovePoints(ship);
            int nearest = NearestDistance(ship, enemies);

            // Ultimates, one per combat: defence once hurt, Mass Torpedo early, Overdrive only as the ship's sole one.
            float shields = ShipCombatInfo.ShieldFraction(ship), hull = ShipCombatInfo.HullFraction(ship);
            if (shields < 0.5f || hull < 0.6f)
            {
                var cast = TrySelf(ship, memory, CORP_CHANT, $"shields {shields:P0}, hull {hull:P0}")
                           ?? TrySelf(ship, memory, EVASION_TACTICS, $"shields {shields:P0}, hull {hull:P0}");
                if (cast != null) return cast;
            }

            int torpedoRange = ShipCombatInfo.WeaponRange(ship, StarshipWeaponType.TorpedoTubes);
            if (torpedoRange > 0)
            {
                var cast = WhenInReach(ship, memory, MASS_TORPEDO, enemies, torpedoRange + mp, "torpedo");
                if (cast != null) return cast;
            }

            int weaponRange = ShipCombatInfo.WeaponRange(ship);
            if (nearest > weaponRange + mp && !HasOtherUltimate(ship))
            {
                var cast = TrySelf(ship, memory, OVERDRIVE, $"no capital ship within reach ({nearest} tiles)");
                if (cast != null) return cast;
            }

            int lanceRange = ShipCombatInfo.WeaponRange(ship, StarshipWeaponType.Lances);
            if (lanceRange > 0)
            {
                var cast = WhenInReach(ship, memory, BOOST_LANCE, enemies, lanceRange + mp, "lance");
                if (cast != null) return cast;
            }

            var augury = ShipCombatInfo.FindAbility(ship, ARC_AUGURY_2, ARC_AUGURY);
            if (augury != null)
            {
                int bonus = ShipCombatInfo.Guid(augury) == ARC_AUGURY_2 ? 3 : 2;
                float reach = weaponRange + mp;
                if (nearest > reach && nearest <= reach + bonus)
                {
                    var cast = TrySelf(ship, memory, ShipCombatInfo.Guid(augury), $"nearest capital ship {nearest} tiles, just out of reach");
                    if (cast != null) return cast;
                }
            }

            bool anyAhead = false;
            foreach (var e in enemies)
                if (ShipCombatInfo.AngleFromBow(ship, e.Position) < 90f) { anyAhead = true; break; }
            if (!anyAhead && mp >= 1f)
            {
                var heading = ShipCombatInfo.FindAbility(ship, NEW_HEADING_2, NEW_HEADING);
                if (heading != null)
                {
                    var cast = TrySelf(ship, memory, ShipCombatInfo.Guid(heading), "every capital ship is abaft the beam");
                    if (cast != null) return cast;
                }
            }
            return null;
        }

        private static ShipCast AfterShot(StarshipEntity ship, TurnMemory memory, List<StarshipEntity> enemies)
        {
            var type = memory.PendingReload;
            memory.PendingReload = null;
            float mp = ShipCombatInfo.MovePoints(ship);

            if (type == StarshipWeaponType.TorpedoTubes)
                return WhenInReach(ship, memory, RAPID_RELOAD_TORPEDO, enemies,
                    ShipCombatInfo.WeaponRange(ship, StarshipWeaponType.TorpedoTubes) + mp, "torpedo", breaksPlan: true);

            if (type != StarshipWeaponType.Macrobatteries) return null;
            var reload = ShipCombatInfo.FindAbility(ship, RAPID_RELOAD, RAPID_RELOAD_2);
            if (reload == null) return null;
            if (ShipCombatInfo.Guid(reload) == RAPID_RELOAD_2 && LanceStillToFire(ship))
            {
                LogOnce(memory, ship, reload, "held: the lances still have a shot and would take -25% to hit");
                return null;
            }
            return WhenInReach(ship, memory, ShipCombatInfo.Guid(reload), enemies,
                ShipCombatInfo.WeaponRange(ship, StarshipWeaponType.Macrobatteries) + mp, "macrobattery", breaksPlan: true);
        }

        private static ShipCast TurnEnd(StarshipEntity ship, TurnMemory memory, List<StarshipEntity> enemies)
        {
            // A volley on the plan's last step never reaches another next-action step.
            if (memory.PendingReload != null)
            {
                var reload = AfterShot(ship, memory, enemies);
                if (reload != null) return reload;
            }

            var shields = ship.Shields;
            if (shields == null || shields.ShieldsMaxSum <= 0) return null;

            // The game's own AI rule for restarting shields (StarshipAIAbilityRestrictionLogic.RestartShields).
            var weakest = shields.WeakestSector;
            bool low = shields.ShieldsSum * 100 / shields.ShieldsMaxSum <= 50
                       || (weakest != null && weakest.Max > 0 && weakest.Current * 100 / weakest.Max <= 20);
            if (low)
            {
                var restart = ShipCombatInfo.FindAbility(ship, RESTART_SHIELDS_2, RESTART_SHIELDS);
                if (restart != null)
                {
                    var cast = TrySelf(ship, memory, ShipCombatInfo.Guid(restart), $"shields at {ShipCombatInfo.ShieldFraction(ship):P0}");
                    if (cast != null) return cast;
                }
            }

            return Reinforce(ship, memory, enemies);
        }

        /// <summary>
        /// Reinforce Shields on the sector facing the most enemy capital ships that can reach us next turn (counted
        /// twice when already within their weapon range), skipping sectors already reinforced.
        /// </summary>
        private static ShipCast Reinforce(StarshipEntity ship, TurnMemory memory, List<StarshipEntity> enemies)
        {
            var parent = ShipCombatInfo.FindAbility(ship, REINFORCE_SHIELDS_2, REINFORCE_SHIELDS);
            if (parent == null) return null;
            string parentGuid = ShipCombatInfo.Guid(parent);
            if (memory.Tried.Contains(parentGuid)) return null;

            var weight = new Dictionary<StarshipSectorShieldsType, int>();
            foreach (var enemy in enemies)
            {
                int dist = enemy.DistanceToInCells(ship);
                int range = ShipCombatInfo.WeaponRange(enemy);
                if (dist > range + ShipCombatInfo.TurnMovePoints(enemy)) continue;
                var sector = ShipCombatInfo.SectorFacing(ship, enemy.Position);
                weight.TryGetValue(sector, out int w);
                weight[sector] = w + (dist <= range ? 2 : 1);
            }
            if (weight.Count == 0)
            {
                LogOnce(memory, ship, parent, "held: no capital ship can reach us next turn");
                return null;
            }

            AbilityData best = null;
            StarshipSectorShieldsType bestSector = StarshipSectorShieldsType.Fore;
            int bestWeight = 0;
            foreach (var variant in parent.GetConversions())
            {
                if (!ReinforceSector.TryGetValue(ShipCombatInfo.Guid(variant) ?? "", out var sector)) continue;
                if (!weight.TryGetValue(sector, out int w) || w <= bestWeight) continue;
                if (SectorShields(ship, sector)?.Reinforced == true) continue;
                if (!variant.IsAvailable || !variant.CanTarget(new TargetWrapper(ship))) continue;
                best = variant; bestSector = sector; bestWeight = w;
            }

            memory.Tried.Add(parentGuid);
            if (best == null)
            {
                Log.Planning.Info($"[Ship] {parent.Name} held: the threatened sectors are reinforced already, or it's unavailable");
                return null;
            }
            Log.Planning.Info($"[Ship] {parent.Name} on the {bestSector} shields ({bestWeight} threat)");
            return new ShipCast { Ability = best, Target = new TargetWrapper(ship) };
        }

        private static StarshipSectorShields SectorShields(StarshipEntity ship, StarshipSectorShieldsType sector) => ship.Shields?.GetShields(sector);

        private static ShipCast WhenInReach(StarshipEntity ship, TurnMemory memory, string guid, List<StarshipEntity> enemies,
            float reach, string weapon, bool breaksPlan = false)
        {
            if (ShipCombatInfo.FindAbility(ship, guid) == null) return null;
            int nearest = NearestDistance(ship, enemies);
            if (nearest > reach)
            {
                LogOnce(memory, ship, ShipCombatInfo.FindAbility(ship, guid), $"held: no capital ship within {weapon} reach ({nearest} tiles)");
                return null;
            }
            var cast = TrySelf(ship, memory, guid, $"capital ship {nearest} tiles away, within {weapon} reach");
            if (cast != null) cast.BreaksPlan = breaksPlan;
            return cast;
        }

        /// <summary>Casts the ability on the ship itself if it has it, hasn't tried it this turn, and it's usable now.</summary>
        private static ShipCast TrySelf(StarshipEntity ship, TurnMemory memory, string guid, string why)
        {
            var ability = ShipCombatInfo.FindAbility(ship, guid);
            if (ability == null || memory.Tried.Contains(guid)) return null;
            var self = new TargetWrapper(ship);
            if (!ability.IsAvailable || !ability.CanTarget(self)) return null;
            memory.Tried.Add(guid);
            Log.Planning.Info($"[Ship] {ability.Name}: {why}");
            return new ShipCast { Ability = ability, Target = self };
        }

        private static bool HasOtherUltimate(StarshipEntity ship)
        {
            foreach (var guid in OtherUltimates)
                if (ShipCombatInfo.FindAbility(ship, guid) != null) return true;
            return false;
        }

        private static bool LanceStillToFire(StarshipEntity ship)
        {
            foreach (var fact in ship.Abilities.RawFacts)
            {
                var data = fact?.Data;
                if (ShipCombatInfo.WeaponType(data) == StarshipWeaponType.Lances && data.IsAvailable) return true;
            }
            return false;
        }

        private static int NearestDistance(StarshipEntity ship, List<StarshipEntity> enemies)
        {
            int best = int.MaxValue;
            foreach (var e in enemies)
            {
                int d = ship.DistanceToInCells(e);
                if (d < best) best = d;
            }
            return best;
        }

        private static void LogOnce(TurnMemory memory, StarshipEntity ship, AbilityData ability, string message)
        {
            if (ability == null) return;
            string key = ShipCombatInfo.Guid(ability) + message;
            if (memory.Logged.Add(key)) Log.Planning.Info($"[Ship] {ability.Name} {message}");
        }

        private static TurnMemory Memory(BaseUnitEntity ship)
        {
            string id = ship.UniqueId ?? "ship";
            if (!_turns.TryGetValue(id, out var memory))
            {
                memory = new TurnMemory();
                _turns[id] = memory;
            }
            return memory;
        }
    }
}
