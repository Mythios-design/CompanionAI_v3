# CompanionAI v3 (Mythios fork)

Warhammer 40,000: Rogue Trader companion AI mod, maintained fork.

This is a fork of [CompanionAI v3 by seyelive5](https://github.com/seyelive5/CompanionAI_v3). It includes everything up to
upstream v3.123.1 and adds a large set of fixes and new behaviour on top. Like the original, it replaces the companion AI in
turn-based combat with a planner that looks at the whole turn: what to buff, where to move, what to attack and what to save
for later.

## What's different in this fork

### Smarter turns and movement
- **Melee characters close in.** Prefer Melee characters walk, charge or jump into melee instead of standing back, and use
  Charge on enemies that are out of walking reach.
- **Ranged characters stay safe.** They keep out of where melee enemies can reach next turn, and use leftover MP at the end
  of the turn to step into cover.
- **Cover counts.** Positions in cover are weighted more heavily, both for attacking and at the end of the turn.
- **Real walking distance.** Approach moves follow the pathfinder's route instead of a straight line, and units head for
  line of sight when nothing they can reach can see an enemy.
- **No free hits.** A unit that is already engaged attacks before it moves, so it doesn't walk out of melee and take
  attacks of opportunity.
- **No wasted turns.** Bonus turns from allies are used properly (they start fresh, and are only granted to an ally who can
  use them), and a unit with AP left makes a last-chance attack before ending its turn.

### Fewer stuck or wasted actions
- Recovers when the game refuses a cast, checks AP before casting, and remembers what failed for the rest of the turn.
- Doesn't recast buffs that are still active, and never uses Desperate Measures.
- Checks friendly fire along a shot's whole line, including the Arc Rifle's trail.
- Character settings stay correct across save loads.

### Weapons
- Every normal turn starts on the primary weapon set; the second set is a fallback when the first can't attack well.
- Weapon swaps account for adjacent enemies, empty sets and engaged-only backup sets.
- Reloads before a burst the magazine can't cover, and tops up guns at the end of the turn.

### Class and archetype abilities
Abilities are matched by their game IDs and planned for when they pay off, not just cast when available:
- **Bladedancer:** Blood Oath on the right target and only before a killing blow, crowd positioning, jumps and Blade Dance,
  Blood Phial and Raining Blood, Acrobatic Artistry at the end of the turn.
- **Officer:** order pairs such as Voice of Command then Bring It Down!, Finest Hour! on the ally who would deal the most
  damage, Word of the Emperor as a group buff.
- **Operative:** debuff the target and then shoot it; Tactical Knowledge collects exploits after the setup and shot.
- **Soldier:** Rapid Fire and Concentrated Fire only before the attack they work on.
- **Pyromancer:** burning-blade melee combo; self-ignite only at high HP.
- **Overseer (cyber-mastiff):** Apprehend every turn on the dog's target, repeated when AP allows, with its rising AP cost
  worked out exactly.
- **Arch-Militant:** Versatility read from the game's own state; Reckless Rush used to reach the enemy and to hit 4
  Versatility stacks for a stance.
- **Bounty Hunter:** Hot on the Trail cast last, on an enemy an ally can finish; Raid and Cull the Bold timed to pay off.
- **Master Tactician:** tactical advantage spent once a turn on the order that's worth it.
- **Executioner:** the damage-over-time abilities wait until enough enemies are bleeding, burning or poisoned.
- **Grand Strategist:** zones placed together by who benefits.

### Heroic Acts
- Per-character toggle to allow or block Heroic Acts.
- A party-wide usage order: #1 uses theirs first, then #2, and so on.

### Space combat (experimental)
With **Ship Combat AI** turned on, the game's own starship AI flies your ship and this mod supplies the decisions it needs:
- Moves every turn, avoids meteor streams and blocked paths, and tries to end behind enemy ships.
- Picks one enemy ship a turn and lines up as many guns on it as possible, moving part way and firing between moves.
- Casts post abilities (shields, reloads, engine tricks) and ultimates at sensible moments.
- Works around a game bug where a strike craft parked on your ship's cell froze it for the rest of the fight.

## Installation

1. Install [Unity Mod Manager](https://www.nexusmods.com/site/mods/21).
2. Download the latest ZIP from this fork's [Releases](https://github.com/Mythios-design/CompanionAI_v3/releases).
3. Drag the ZIP into Unity Mod Manager, enable the mod and start the game.

Remove or disable the original CompanionAI v3 first; only one copy should be installed.

## Settings

Open the mod's page in Unity Mod Manager (Ctrl+F10 in game):

| Setting | Options | What it does |
|---------|---------|--------------|
| AI enabled | On / Off, per character | Lets the AI play that character in combat |
| Role | Auto, DPS, Tank, Support, Overseer | Auto picks a role from the character's abilities |
| Range preference | Adaptive, Prefer Melee, Prefer Ranged | Adaptive follows the equipped weapon |
| Durable | On / Off, per character | Comfortable fighting close to enemies |
| Allow Heroic Acts / Heroic Act order | Per character | Whether and when the AI uses Heroic Acts |
| Weapon set rotation | Per character | Lets the AI switch to the second weapon set |
| Ship Combat AI | On / Off | Hands your ship to the AI in space combat |

## Requirements

- Warhammer 40,000: Rogue Trader
- Unity Mod Manager 0.23.0 or newer

## Credits and license

- Original mod: [seyelive5/CompanionAI_v3](https://github.com/seyelive5/CompanionAI_v3). All credit for the
  original design and most of the code goes to its author.
- Fork changes: Mythios, developed with [Claude Code](https://claude.com/claude-code).
- MIT License, as the original.
