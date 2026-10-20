# Archetype Guidelines for AI Decisions

How the companion AI should play each archetype, so new rules for skills and positioning follow one plan instead of being decided case by case.

Rules are tied to abilities (by GUID), not to characters, so a guideline applies to anyone who has that archetype's abilities.

Lines marked **Decision** record your answers from 2026-10-17. Lines marked **To build** are decisions that need a code change.

- Status as of commit c3136d8 (2026-10-17).
- Sources: `docs/rogue-trader-abilities.md`, the party ability dump (`AllAbilities_Dump.txt`) and combat logs.

## Contents

1. [Team-wide principles](#team-wide-principles)
2. [Bladedancer (Kibellah)](#bladedancer-kibellah)
3. [Officer (Cassia)](#officer-cassia)
4. [Operative (Thordan, Idira)](#operative-thordan-idira)
5. [Soldier (Argenta)](#soldier-argenta)
6. [Warrior (Abelard)](#warrior-abelard)
7. [Psyker powers (Idira)](#psyker-powers-idira)
8. [Decisions](#decisions)

---

## Team-wide principles

These apply to everyone and come before any archetype rule.

### 1. Follow the game's rules, read from the game

- **One weapon attack per turn.** Single Shot, Burst Fire, Strike and the like share one cooldown, the weapon attack group. Extra attacks come only from what the game allows: free uses (Run and Gun, Never Stop Shooting, Blood Oath's kill reward, Finest Hour!), cooldown override buffs, or ignore-cooldown rules. `WeaponAttackAllowance` reads these. Never plan more weapon attacks than it allows.
- **Once-per-turn abilities are learned.** If an ability goes on cooldown after one cast, `OncePerTurnAbilities` remembers it and never plans it twice in one turn.
- **Abilities are matched by GUID, checked against the party dump.** Never add an ID without confirming it in the dump. Watch for other versions of the same ability (upgraded, DLC, enemy copies).

### 2. Spend AP in priority tiers

From highest to lowest:

1. **The turn's main attack**, usually the one weapon attack.
2. **Reserved setups.** AP set aside at the start of planning for something that must happen, like Raining Blood when the Blood Phial is empty.
3. **Setups placed directly before the attack**: Operative debuffs, Voice of Command with Bring It Down!, Raining Blood.
4. **Other buffs, debuffs, heals and orders.**
5. **Leftover spends.** Only AP left after everything else, like the Blood Phial throw.

**Approach by walking distance:** when no tile this turn can attack, a unit walks toward the nearest place it can attack from, measured by real walking distance (one walking map per turn, about four turns of movement deep, from the game's own pathfinding). It keeps last turn's enemy, else the planner's target, if that is within one turn's walk of the nearest; otherwise the nearest by walking. Its step this turn is the furthest safe tile on that walk. If no enemy is reachable on foot (across a pit, on a platform), it doesn't approach at all: no straight-line or dead-end walks (`WalkingApproach.cs`).

**Last-chance attack:** before any turn ends with AP left, one more look for an attack the game still allows (weapon attacks with free uses left, psychic powers, Blade Dance), from where the unit stands or after a walk that draws no attacks of opportunity, with the usual friendly-fire rules. Grenades, markers, Blood Phial, Raining Blood, Tactical Knowledge, Heroic Acts and Desperate Measures are left to their own rules. When AP stays unspent, the log says why (`LastChanceAttack.cs`).

A setup is only worth AP if the thing it sets up actually happens this turn.

### 3. Never hit allies

- `maxPlayerAlliesHit` is 0. No area attack, chain or random-target ability may hit an ally.
- This covers arc weapon chains (checked at the target's location), scatter and burst fire, self-centred attacks like Blade Dance, and cones.
- If allies block an attack, move to a tile where they don't, rather than accepting the risk.

### 4. Bonus turns go to allies who can use them

A granted turn is wasted on an ally who can't act with it. `TurnGrantPolicy` decides:

- **AP-only grants** (Bring It Down!): the ally must be engaged in melee (melee characters) or able to shoot from where they stand (ranged characters).
- **Movement-only grants:** not given to a melee ally who already has an enemy in reach.
- **Melee or ranged:** decided by the character's range setting first; on Adaptive, anyone with a ranged attack or power counts as ranged.

### 5. Positioning by role

- **Ranged characters** end turns out of reach of melee enemies when they can, in cover from as many ranged enemies as possible, and don't move closer just to shoot the same targets they could already hit.
- **Cover:** own cover counts double in tile choice. With MP left at the end of a plan, a ranged character who isn't engaged steps to the nearest tile with better cover, as long as it draws no attacks of opportunity, enters no hazard, isn't in reach of more melee enemies, and keeps as many enemies in weapon range (`CoverPositioning.cs`). Tanks stay visible.
- **Melee characters** go where they hit hardest. For Bladedancers that means next to as many enemies as possible and no allies (see below).
- **Line of sight:** a ranged character with no visible enemy heads for a tile that can see one.

---

## Bladedancer (Kibellah)

**Core idea:** be in melee with as many enemies as possible, as often as possible. Kills refund mobility and AP; wounds pay for the oaths. Being surrounded makes her a poor target for ranged enemies and gets the most out of Blade Dance.

| Ability | GUID | Notes |
|---|---|---|
| Blade Dance | `e955823f54d24088ae1fdefe88d3684d` | 2 AP, 1-round cooldown. Each attack picks a random target among every creature within 1 cell, **allies included**, and breakable objects that aren't cover. Doesn't count toward the attack limit. Needs a sword. Clears her MP (doesn't end the turn). |
| Blood Oath | `590c990c1d684fd09ae883754d28a8ac` (buff on the enemy `91028e85...`) | 0 AP, any range, costs 5 + half her level in wounds. The enemy can't dodge or parry her. The buff is removed by her next attack roll on it, kill or not (Blade Dance excepted: a dance kill pays, a dance hit that doesn't kill removes the oath when the dance ends). A kill on a sworn enemy heals 5 + 2 x Toughness bonus; the round's first such kill also gives +1 AP and an extra weapon attack (the game marks it with buff `44f1d979...` on her). One oath per enemy; no per-turn limit in the game. |
| Death From Above | `6f1b7cfb48a0450cb85ce8a8879502de` (+ 3 variants) | 0 AP and a charge (1 at combat start, +1 per kill not made by the jump), range 3, no attacks of opportunity (overwatch still fires). The landing makes an attack roll on every enemy within 1 cell for a flat Weapon Skill bonus + Agility bonus, ignoring line of sight; it never hits allies and doesn't use the weapon attack. |
| Death Waltz | | Ultimate (Heroic Act). |

**Priorities:**

1. **Blood Oath** only right before an attack that should kill: on the lowest-HP enemy one of her affordable melee attacks can kill on its best damage roll (the oath's attack can't be dodged or parried), hittable from where she stands if she's engaged, otherwise within her walking reach this turn (a jump doesn't count: she jumps first and swears after landing). Who is sworn is read from the game's buff, so an oath spent by a hit that didn't kill frees her to swear again. At most one oath a turn, and none while a sworn enemy she can reach is alive. Her attacks (single-target, Brutal Strike's aim, kill sequences) go at the sworn enemy. No HP limit on casting it.
2. **Death From Above as an attack** when the landing touches 2 or more enemies. Never a landing next to a sworn enemy it won't kill: its attack roll would spend the oath (strike, gap closer, crowd jump and Acrobatic Artistry's replay all check this).
3. **Death From Above as a gap closer** when the target is in range and a melee attack is still usable after landing.
4. **Blade Dance** whenever enough enemies are adjacent and no ally is. Adjacent breakable objects take a share of the attacks (enemies scaled by enemies / (enemies + objects)). With a sworn enemy adjacent, her single-target attack on it goes first. If an ally blocks it, step to a tile touching enemies and no allies first.
5. **End of turn (and the end of every plan):** with MP left, move to the tile touching the most enemies and no allies, at least one more enemy than now (not below 40% HP). Walking stays inside the enemies' threatened tiles, which draws no attacks of opportunity. A better tile outside that space is reached with Death From Above (0 AP, no attacks of opportunity) if it has a use left. When she keeps MP unused, the log says why.

6. **Acrobatic Artistry** (1 AP, doesn't end the turn) at the end of the turn, never inside a plan. The game replays this turn's jumps newest first, each landing back where that jump took off, then walks her free to her turn-start cell, with no attacks of opportunity. Used for **replay** when those take-off spots touch living enemies and no allies, and the replay's estimated damage beats her best remaining attack (otherwise the attack goes first). Used to **escape** below 40% HP when the turn-start cell is safer (fewer melee enemies reach it, or equal and better cover), after her attacks are spent; no end-of-turn advance follows an escape.

**Positioning:** +40 per adjacent enemy, -80 per adjacent ally, on melee attack tiles and jump landings (on gap-closer landings the ally penalty only applies when Blade Dance is available, since the landing never hits allies). Long walks are fine; the archetype is built for movement.

**Built:** `BladedancerTactics.cs` (oath choice and reward tracking, Death From Above rules, holding ally heals during an oath), `BladedancerPositioning.cs` (tile scoring, stepping clear, engagement move), `AcrobaticArtistryPlanner.cs` (replay and escape test, called from the orchestrator's last-chance step).

**Not built yet:**

- Abilities not in the party yet: Captive Audience, Veil of Blades, Oath of Vengeance. Acrobatic Artistry is built; she has it now, untested in play (until this fix the analyzer dropped it as unclassified).

> **Decision (final, 2026-10-18):** option A. The game pays the oath back only if her *next* attack on the sworn enemy kills it, and each oath costs 14 to 28% of her HP. So she swears only on the lowest-HP enemy her next attack can kill, at most once a turn, and attacks it next. "Can kill" means on her best damage roll, not her average (changed after a fight where her average hit never reached any enemy's HP and she swore no oath). Earlier the same day the rule was "an oath every turn on any enemy in reach" with no HP limit; in tests that cost her up to 55% HP in one turn with nothing back. No HP limit. The leftover-AP step never casts Blood Oath.

> **Decision:** yes, 40% HP stays the line for the end-of-turn move into a crowd.

> **Decision:** no special rule. Death Waltz stays a generic Heroic Act.

### Death Cult Assassin origin (Kibellah)

| Ability | GUID | Rule |
|---|---|---|
| Raining Blood | `e299c13f2278479bbd4e23269af5657c` | Blood Phial refill. Only while the phial is empty, only right before a single-target melee attack this turn. 1 AP reserved for it. |
| Blood Phial (item) | `29dd732891604e3db9913065a7f4e2d0` | Lowest-priority spend. Thrown where the 3×3 pool covers the most enemies (2 each) and allies (1 each). Not thrown once no enemies are left. Held before the melee range filter, which would otherwise drop it as a ranged grenade. |

**Built:** `RainingBloodPlanner.cs`, `BloodPhialPlanner.cs`.

---

## Officer (Cassia)

**Core idea:** give allies more turns and better turns. Voice of Command unlocks the bonus effect of every other order and removes range limits.

| Ability | GUID | Notes |
|---|---|---|
| Voice of Command | `9c78e44bf8ff44a9afff8370c673c9ad` | 1 AP, range 10. Stats up for 1 round. Same ally can't be targeted again for 2 rounds. Once per turn. |
| Bring It Down! | `5bfccf14d8604ae1a041fb347fb8f19f` | 2 AP, range 4 (any range under VoC). Ally gets an extra turn with 2 AP, no MP. With VoC, a kill gives momentum. |
| Finest Hour! | `33c8c9db91694ca6ad3eff26e36dd0af` | Ultimate. Ally gets a full extra turn with no attack limit. |

**Priorities:**

1. **Bring It Down! comes before Voice of Command.** First choose the Bring It Down! ally (engaged melee ally, or a ranged ally who can shoot from where they stand).
2. **AP for both:** Voice of Command on that ally, then Bring It Down!. That also reaches allies beyond 4 tiles.
3. **AP for one:** Bring It Down! only.
4. **No ally worth a bonus turn:** Voice of Command on the best damage dealer, rotating allies because of the 2-round lockout.

**Built:** `OfficerOrders.cs`, `TurnGrantPolicy.cs`, `TurnGrantDetector.cs`.

**Not built yet:**

- Finest Hour!'s granted points can't be read (they're calculated), so it isn't checked and goes to the first ally in the list. In one fight it went to Kibellah, who had no one to reach.
- Abilities not in the party yet: Take Aim!, Break Their Ranks!, Air of Authority, Move Move Move!, To Me!.

> **Decision:** Finest Hour! goes to the ally who would deal the most damage with a full turn (full AP and MP, no attack limit). **To build.**

### Navigator (Cassia)

| Ability | Notes |
|---|---|
| Lidless Stare | 2 AP, range 10 cone. Damages and stuns everything in the cone, **allies included**. Aimed by the generic cone planner. |

> **Decision:** deferred. Cassia needs more Navigator abilities before there's anything to test. For now: Lidless Stare on the biggest group with no allies in the cone.

---

## Operative (Thordan, Idira)

**Core idea:** build exploits, then consume them with Expose Weakness right before the big hit, so the shot lands on a target with lowered defences.

| Ability | GUID | Notes |
|---|---|---|
| Analyse Enemies | `502b9195c84c42c5b180f3f63ed7955c` (+ 3 variants) | 1 AP. Adds exploits. |
| Expose Weakness | `197b8a8a12b0442db7ffee1067cf3d97` | 1 AP. Removes exploits: less dodge, parry and armour until the Operative's next turn. |
| Dismantling Attack | | Ultimate, plus a Desperate Measure version. The AI never uses Desperate Measures. |

**Priorities (one-target turn):** Analyse Enemies → Expose Weakness → Death Sentence (Thordan) → the shot, all on the same enemy, as AP allows. With less AP: Expose first, then Analyse (only with Expose), then Death Sentence. Spare AP goes on Analyse Enemies for the team's next target.

**Built:** `OperativeSequence.cs`. It runs for anyone with these abilities, so Idira gets it too (for example Expose Weakness → Lightning Arc).

**Not built yet:**

- The damage estimate in Thordan's turn strategy still assumes four shots per turn ("Single×4"). That's cosmetic now that his turn is rearranged afterwards, but it skews the choice between strategies.
- Expose Weakness is sometimes cast again on a target that already has it, as a leftover-AP spend.
- Abilities not in the party yet: Precise Attack, Perfect Spot, Joint Analysis.
- **Tactical Knowledge** (Pasqal, `2a417cde46674ae0a5da8dcfd9ec621d`): after his setup and shot, centred on the enemy that removes the most exploits (all stacks of a lone enemy in the 5-cell circle, or 1 per enemy). The buff replaces itself, so it is cast only when it removes more than the buff already active this combat. 4 or more removed may take the AP of a leftover Analyse Enemies. Built: `TacticalKnowledgePlanner.cs`.
- **Perfect Spot** (`d0f95ed472d542579026e8a8b8eb2bab`, buff `bca05f18...`): +15 Ballistic Skill, +15 Perception and +25% cover strength until combat ends, removed the moment the unit moves. Cast first in the turn, only when the plan attacks without moving and the tile has cover; otherwise dropped (a move would waste it). While it is on: attack from here over walking to another attack tile, no cover step, no end-of-turn reposition, no move into Death Sentence range. Built: `PerfectSpotPlanner.cs`.

> **Decision:** Idira is an Operative, so the one-target turn applies to her as built.

### Arbitrator Castigator origin (Thordan)

| Ability | GUID | Rule |
|---|---|---|
| Death Sentence | `6a4c3b65dff840e0aab5966ffe8aa7ba` | 1 AP, range 10. More crit chance on the target, growing with each hit. Part of the one-target turn, placed right before the shot. |

### Thordan's weapons

- **Arc Rifle:** its chain is blocked when an ally stands within 2 tiles of the target. That depends only on the target's location, so moving doesn't fix it.
- **Long-Las:** the fallback when the Arc Rifle is blocked.
- **Swap rule:** switch when the set in hand can't attack anyone and the other set can, from here or after moving.
- **No swap for a bonus attack:** a swap counts as giving a free attack only if a weapon-switch trigger on the unit adds a bonus use. None in the class, gear, weapon, unit, buff or DLC blueprints does (the only one, on the Deflection Gloves, adds deflection), so the end-of-turn swap "for rotation" no longer happens.

> **Decision:** unsure for now. Thordan keeps the current rule (switch to the Long-Las when the Arc Rifle can't attack anyone). Revisit with a dump taken with the Long-Las in hand.

---

## Soldier (Argenta)

**Core idea:** mobile firepower. Run and Gun turns one attack into two and adds MP. Kills unlock Revel in Slaughter.

| Ability | Notes |
|---|---|
| Run and Gun | +MP, next attack costs 1 less AP and doesn't count toward the attack limit. Winded next turn. |
| Rapid Fire | Next burst has double rate of fire, -25% damage, random trajectories (an ally risk). |
| Concentrated Fire | `81ccaba57178473da52e89e99a42392a`. Buff for her next ranged pattern attack that isn't scatter (shotguns, flamers, Blast Wave's Rending Shots); Single Shot and Burst Fire don't use it. 1 AP, 1-round cooldown. |
| Revel in Slaughter | After 3 kills: removes winded, adds BS and crit. |
| Firearm Mastery | Ultimate. Extra free attacks this turn, so only cast with an enemy to attack from where she stands (0 AP: the replan after a move can still cast it). |

**Priorities:**

1. **Attack, Run and Gun, attack again.** The second attack is planned in the replan after Run and Gun.
2. **Burst Fire** only where the scatter can't reach allies.
3. **Stay out of melee reach.** Don't move closer to shoot targets she can already hit.

**Built:** weapon attack cap and free uses (`WeaponAttackAllowance.cs`), weapon swap rules (`BasePlan.WeaponRotation.cs`, `WeaponSetForecast.cs`), end-of-turn cover.

**Attack-type buffs and the free Soldier ability** (`SoldierBuffPlanner.cs`): Rapid Fire goes directly before a planned scatter attack (Burst Fire), Concentrated Fire directly before a planned ranged pattern attack; neither while its buff is still on her (it waits for that attack), and neither without such an attack. The first Soldier career ability in a combat is free (no other Soldier in the party), and until it is used the game reports 0 AP for all of them, so only the first one in a plan is booked free and the rest at full cost.

**Not built yet:** the fix for Argenta ending her turn within a Thief Brute's reach was proposed but not built. She moved closer to shoot the same 5 enemies she could already hit.

> **Decision:** Blast Wave is for when she can't use her primary weapon, which means when she's engaged in melee (her rifle's attacks can't fire next to enemies). Otherwise she stays on Unfading Valour. **To build** (check the current swap rules match this exactly).

### Adepta Sororitas origin (Argenta)

- **Furious Recital:** 0 AP, once per combat, momentum and zeal. Used as a generic pre-attack buff.

> **Decision:** use Furious Recital at the start of combat, or right before a heavy burst-fire attack, to build momentum fast, stack damage bonuses and wipe out groups. **To build** (check the current pre-attack timing matches).

---

## Warrior (Abelard)

**Core idea:** draw attacks and get paid for them. Tank the enemy so squishier allies aren't targeted.

| Ability | Notes |
|---|---|
| Taunting Scream | Area taunt for 3 rounds. Point-targeted; aim point now checked for line of sight. |
| Charge | 2 AP, range 6, needs 2 free cells. Doesn't count toward the attack limit. |
| Endure | Deflection and temporary wounds until next turn. Once per turn. |
| Daring Breach | Ultimate / AP refund ability. |

**Priorities:**

1. **Endure** before drawing attacks.
2. **Taunting Scream** on the group threatening squishy allies.
3. **Charge** into reach, then attack.

**Built:** smart taunt with line-of-sight check (`TauntScorer.cs`), Break Through vs Charge rule (`AbilityPolicy.cs`, from the original author).

### Navy Officer origin (Abelard)

- **Brace for Impact!:** 0 AP, braces himself and allies within 5 cells for 1 round. Used as a free buff.

> **Decision:** use Brace for Impact! whenever it's free, but preferably when allies are within its 5 cells, and before he moves away from them. **To build.**

> **Decision:** setting up taunts is better than holding position. No change.

---

## Sanctic powers (Heinrix)

- **Word of the Emperor** (Sanctic, `6285e5ba2a804e3d9df1dd5f7fa819f2`): 1 AP, point within 12 cells, stacking Resolve for allies within 5 cells until combat ends; raises the Veil by 1. A group buff with leftover AP only: on the point covering the most allies, at least 3 (Heinrix counts), and only while the Veil is in the safe range. Never filler. Built: `WordOfTheEmperorPlanner.cs`.

## Psyker powers (Idira)

| Ability | Notes |
|---|---|
| Forewarning (Divination) | 1 AP, +dodge and parry until end of combat. Goes on the ally about to be attacked. |
| Psychic Shriek (Telepathy) | 2 AP, single-target damage. |
| Lightning Arc (staff) | Chain attack; same ally chain rule as the Arc Rifle. |

> **Decision:** Forewarning goes on the tank and melee allies first, preferring whoever is most threatened, and only once per ally per combat. **To build.**

---

## Decisions

Answers from 2026-10-17, in question order.

| # | Topic | Decision | Code change |
|---|---|---|---|
| 1 | Blood Oath on enemies she can't kill this turn | No (final 2026-10-18): only on the lowest-HP enemy her next attack can kill on a top roll, once a turn, attacked next, no HP limit | Built |
| 2 | 40% HP line for the Bladedancer's engagement move | Yes, keep 40% | None |
| 3 | Rule for Death Waltz | No, generic Heroic Act | None |
| 4 | Who gets Finest Hour! | The ally who would deal the most damage with a full turn | To build |
| 5 | Cassia's Navigator priorities | Deferred until she has more Navigator abilities | None yet |
| 6 | Idira's archetype | Operative | None |
| 7 | When Thordan prefers the Long-Las | Unsure; keep the current rule | None yet |
| 8 | Argenta's Blast Wave set | Only when she can't use her primary weapon (engaged in melee) | To build / verify |
| 9 | Furious Recital timing | Start of combat, or right before a heavy burst-fire attack | To build / verify |
| 10 | Brace for Impact! timing | When free, preferably with allies within 5 cells and before he moves away | To build |
| 11 | Abelard: hold position or set up taunts | Setting up taunts is better | None |
| 12 | Forewarning | Tank and melee allies first, most threatened first, once per ally per combat | To build |

### Still to build (not decided here)

- Ending turns within melee reach in general (Argenta, Cassia): the specific cases are fixed (no closer move for the same targets, cone firing tiles weigh melee reach); a general end-of-turn check is not built.
- Thordan: the turn strategy's damage estimate still assumes four shots ("Single×4").
