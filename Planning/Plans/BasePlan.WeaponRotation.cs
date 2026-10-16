using System.Collections.Generic;
using CompanionAI_v3.Analysis;
using CompanionAI_v3.Core;
using CompanionAI_v3.GameInterface;
using CompanionAI_v3.Logging;

namespace CompanionAI_v3.Planning.Plans
{
    public abstract partial class BasePlan
    {
        #region Weapon Set Rotation (v3.9.72, v3.9.74)

        /// <summary>
        /// ★ v3.9.72: 무기 세트 전환 계획
        ///
        /// 설계: 대체 세트로의 전환만 계획 (구체적 공격 X)
        /// 전환 실행 후 plan이 완료되면, TurnOrchestrator의 자연 re-analysis 사이클에서
        /// 새 무기 세트의 능력으로 fresh plan 생성 → 공격 자동 계획
        ///
        /// 이전 설계(전환+공격 한꺼번에 계획)의 문제:
        /// - 비활성 세트 능력의 AbilityData는 Fact 비활성 → IsRestricted=true
        /// - 임시 전환으로 수집한 AbilityData도 전환 복원 후 stale
        /// </summary>
        protected List<PlannedAction> PlanWeaponSetRotationAttack(Situation situation, ref float remainingAP)
        {
            var actions = new List<PlannedAction>();

            if (!situation.WeaponRotationAvailable) return actions;

            // 전환 횟수 상한 체크
            var rotationConfig = Settings.AIConfig.GetWeaponRotationConfig();
            var turnState = Core.TurnOrchestrator.Instance?.GetCurrentTurnState();
            if (turnState != null && turnState.WeaponSwitchCount >= rotationConfig.MaxSwitchesPerTurn)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] WeaponRotation: Max switches reached ({turnState.WeaponSwitchCount}/{rotationConfig.MaxSwitchesPerTurn})");
                return actions;
            }

            // AP가 최소 1 이상 남아야 전환 후 공격 가능
            if (remainingAP < 1f) return actions;

            int alternateSet = situation.CurrentWeaponSetIndex == 0 ? 1 : 0;
            var alternateSetData = situation.WeaponSetData?[alternateSet];
            string alternateName = alternateSetData?.PrimaryWeaponName ?? $"Set{alternateSet}";

            // WeaponSwitch만 계획 — 전환 후 re-analysis에서 공격 자동 계획
            var switchAction = PlannedAction.WeaponSwitch(alternateSet,
                $"Switch to {alternateName} for rotation");

            actions.Add(switchAction);

            Log.Planning.Info($"[{RoleName}] ★ Weapon rotation planned: Switch to Set {alternateSet} ({alternateName}) — " +
                $"attacks will be planned after re-analysis (AP={remainingAP:F1})");

            return actions;
        }

        /// <summary>
        /// ★ v3.9.74: Switch-First 조건 판단
        /// 현재 무기가 무용하고 대체 무기가 도움이 될 때 true
        ///
        /// 전제 조건: 호출자가 WeaponRotationAvailable && !HasHittableEnemies를 이미 체크
        /// 이 메서드는 무기 타입 비교만 수행
        /// </summary>
        protected bool ShouldSwitchFirst(Situation situation)
        {
            if (situation.WeaponSetData == null) return false;

            var engagement = EngagementSwitchDecision(situation);
            if (engagement.HasValue) return engagement.Value;

            int currentIdx = situation.CurrentWeaponSetIndex;
            int altIdx = currentIdx == 0 ? 1 : 0;

            if (currentIdx >= situation.WeaponSetData.Length || altIdx >= situation.WeaponSetData.Length)
                return false;

            var currentSet = situation.WeaponSetData[currentIdx];
            var altSet = situation.WeaponSetData[altIdx];

            if (!altSet.HasWeapons) return false;

            // Case 1: 현재 근접 전용 + 적이 먼 거리 + 대체에 원거리 무기
            if (currentSet.HasMeleeWeapon && !currentSet.HasRangedWeapon
                && altSet.HasRangedWeapon
                && situation.NearestEnemyDistance > 3f)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: melee only, enemy at {situation.NearestEnemyDistance:F1} tiles, alt has ranged");
                return true;
            }

            // Case 2: 재장전 필요 + 대체 세트에 무기 있음 — unless reloading still leaves AP to attack.
            if (situation.NeedsReload && altSet.HasWeapons && !CanReloadAndStillAttack(situation))
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: needs reload, alt has weapons");
                return true;
            }

            // Case 3: 무기 타입이 다름 (근접↔원거리)
            bool currentIsRangedOnly = currentSet.HasRangedWeapon && !currentSet.HasMeleeWeapon;
            bool altIsRangedOnly = altSet.HasRangedWeapon && !altSet.HasMeleeWeapon;
            if (currentIsRangedOnly != altIsRangedOnly)
            {
                // ★ v3.9.78: 원거리→근접 전환 시 도달 가능성 검증
                // 적이 근접 도달 불가 거리면 전환 무의미 — 포지셔닝이 나음
                // 근접 도달 = 근접 사거리 + 남은 MP (이동 후 공격 가능)
                if (currentIsRangedOnly && altSet.HasMeleeWeapon)
                {
                    float meleeReach = altSet.PrimaryWeaponRange > 0 ? altSet.PrimaryWeaponRange : 2f;
                    float mp = CombatAPI.GetCurrentMP(situation.Unit);
                    if (situation.NearestEnemyDistance > meleeReach + mp)
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: Case 3 ranged→melee skip — " +
                            $"enemy at {situation.NearestEnemyDistance:F1} > melee reach {meleeReach:F0} + MP {mp:F1}");
                        // Case 4로 fall through — altRange 체크에서 재판단
                    }
                    else
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: ranged→melee, enemy reachable " +
                            $"(dist={situation.NearestEnemyDistance:F1} <= reach {meleeReach:F0} + MP {mp:F1})");
                        return true;
                    }
                }
                else
                {
                    // 근접→원거리: Case 1이 안 잡은 나머지 (적 ≤ 3f 등)
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: melee→ranged type mismatch");
                    return true;
                }
            }

            // ★ v3.9.76: Case 4: 같은 타입(둘 다 원거리 등)이지만 현재 무기가 hittable=0
            // 대체 무기 사거리가 최근접 적에 도달 가능하면 전환 시도
            // 예: 현재 AoE 화염방사기(아군 안전 차단) → 대체 단일타겟 볼터
            float altRange = altSet.PrimaryWeaponRange;
            if (altRange > 0 && situation.NearestEnemyDistance <= altRange)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchFirst: same type but current unhittable, alt range {altRange:F0} >= enemy dist {situation.NearestEnemyDistance:F1}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// ★ v3.19.0: 현재 무기가 공격 가능하지만 대체 무기가 확연히 더 효율적인지 판단
        /// Phase 1.55 조건 완화용 — HasHittableEnemies=true일 때만 호출
        ///
        /// 전환 조건 (보수적):
        /// 1. 근접 전용인데 대체 세트에 원거리 있고, 원거리 적이 많음
        /// 2. 현재 무기 사거리가 짧아서 1명만 공격 가능, 대체 무기는 여러 적 공격 가능
        /// </summary>
        protected bool ShouldSwitchForEffectiveness(Situation situation)
        {
            if (situation.WeaponSetData == null || !situation.HasHittableEnemies)
                return false;

            var engagement = EngagementSwitchDecision(situation);
            if (engagement.HasValue) return engagement.Value;

            int currentIdx = situation.CurrentWeaponSetIndex;
            int altIdx = currentIdx == 0 ? 1 : 0;
            if (currentIdx >= situation.WeaponSetData.Length || altIdx >= situation.WeaponSetData.Length)
                return false;

            var currentSet = situation.WeaponSetData[currentIdx];
            var altSet = situation.WeaponSetData[altIdx];

            if (!altSet.HasWeapons) return false;

            // Case 1: 현재 근접 전용 + Hittable 1명 이하 + 대체에 원거리 무기
            // 근접으로 1명만 때릴 수 있지만, 원거리로 전환하면 여러 적 공격 가능
            if (currentSet.HasMeleeWeapon && !currentSet.HasRangedWeapon
                && altSet.HasRangedWeapon
                && situation.HittableEnemies.Count <= 1
                && situation.Enemies.Count >= 3)
            {
                // 대체 무기 사거리 내 적 수 확인
                float altRange = altSet.PrimaryWeaponRange;
                if (altRange > 0)
                {
                    int enemiesInAltRange = 0;
                    for (int i = 0; i < situation.Enemies.Count; i++)
                    {
                        var enemy = situation.Enemies[i];
                        if (enemy != null && enemy.IsConscious &&
                            CombatCache.GetDistanceInTiles(situation.Unit, enemy) <= altRange)
                            enemiesInAltRange++;
                    }

                    if (enemiesInAltRange >= 3)
                    {
                        if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchForEffectiveness: " +
                            $"melee hittable={situation.HittableEnemies.Count}, alt ranged can hit {enemiesInAltRange} enemies");
                        return true;
                    }
                }
            }

            // Case 2: 현재 원거리인데 Hittable 0~1명, 대체 근접이 더 많은 적을 때릴 수 있을 때
            // (적이 가까이 모여있는데 현재 원거리 무기로는 사선/안전 문제로 적중 불가)
            if (currentSet.HasRangedWeapon && !currentSet.HasMeleeWeapon
                && altSet.HasMeleeWeapon
                && situation.HittableEnemies.Count <= 1)
            {
                float meleeReach = altSet.PrimaryWeaponRange > 0 ? altSet.PrimaryWeaponRange : 2f;
                int enemiesInMeleeRange = 0;
                for (int i = 0; i < situation.Enemies.Count; i++)
                {
                    var enemy = situation.Enemies[i];
                    if (enemy != null && enemy.IsConscious &&
                        CombatCache.GetDistanceInTiles(situation.Unit, enemy) <= meleeReach)
                        enemiesInMeleeRange++;
                }

                if (enemiesInMeleeRange >= 2)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] ShouldSwitchForEffectiveness: " +
                        $"ranged hittable={situation.HittableEnemies.Count}, alt melee can hit {enemiesInMeleeRange} enemies in melee range");
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when a conscious enemy is adjacent (within melee threat range).</summary>
        protected static bool IsEngaged(Situation situation)
        {
            if (situation?.Enemies == null || situation.Unit == null) return false;
            foreach (var enemy in situation.Enemies)
            {
                if (enemy == null || !enemy.IsConscious || enemy.LifeState.IsDead) continue;
                if (CombatAPI.GetDistanceInTiles(situation.Unit, enemy) <= 1.5f) return true;
            }
            return false;
        }

        /// <summary>
        /// Switching driven by adjacent enemies:
        ///  - Engaged and the other set attacks better next to enemies (e.g. shotgun or pistol vs rifle) → switch.
        ///  - Engaged and the other set is no better → don't switch (never swap to a rifle that can't fire).
        ///  - Not engaged, on the backup set, and the main loadout can reach an enemy → switch back.
        /// Returns null when this rule has no opinion and the other switch rules decide.
        /// </summary>
        protected bool? EngagementSwitchDecision(Situation situation)
        {
            var data = situation.WeaponSetData;
            if (data == null || data.Length < 2) return null;
            int cur = situation.CurrentWeaponSetIndex;
            int alt = cur == 0 ? 1 : 0;
            var curSet = data[cur];
            var altSet = data[alt];
            if (!altSet.HasWeapons) return null;

            if (IsEngaged(situation))
            {
                if (altSet.EngagedRating > curSet.EngagedRating)
                {
                    Log.Planning.Info($"[{RoleName}] Weapon switch: enemy adjacent — {curSet.PrimaryWeaponName} " +
                        $"({Data.WeaponSetAnalyzer.EngagedRatingText(curSet.EngagedRating)}) → {altSet.PrimaryWeaponName} " +
                        $"({Data.WeaponSetAnalyzer.EngagedRatingText(altSet.EngagedRating)})");
                    return true;
                }
                if (altSet.EngagedRating < curSet.EngagedRating) return false;
                return null;
            }

            int home = Data.WeaponSetAnalyzer.GetHomeSet(situation.Unit);
            if (cur != home && alt == home)
            {
                float nearest = situation.NearestEnemyDistanceTiles;
                bool homeReaches = (altSet.HasRangedWeapon && altSet.PrimaryWeaponRange > 0 && nearest <= altSet.PrimaryWeaponRange)
                    || (altSet.HasMeleeWeapon && nearest <= situation.CurrentMP + 2f);
                if (homeReaches)
                {
                    Log.Planning.Info($"[{RoleName}] Weapon switch: no enemy adjacent — back to main weapon {altSet.PrimaryWeaponName}");
                    return true;
                }
            }
            return null;
        }

        /// <summary>True when reloading the current weapon still leaves enough AP to attack with it.</summary>
        private static bool CanReloadAndStillAttack(Situation situation)
        {
            var reload = situation.ReloadAbility;
            if (reload == null || situation.HasReloadedThisTurn) return false;
            float reloadCost = CombatAPI.GetAbilityAPCost(reload);
            return situation.CurrentAP >= reloadCost + 1f;
        }

        /// <summary>
        /// ★ v3.9.74: 대체 무기가 현재 위치에서 적에게 도달 가능한지 판단
        /// Phase 9.5 (Switch-After) 전용 — 전환 후 공격 가능 여부 사전 확인
        ///
        /// 도달 가능 조건:
        /// 1. 대체 원거리 무기 사거리 ≥ 최근접 적 거리
        /// 2. 대체 근접 무기 + 적이 근접 사거리 내 (3타일)
        /// 3. MP 남아서 이동 가능 (전환 후 re-analysis에서 이동+공격 계획 가능)
        /// </summary>
        protected bool CanAlternateWeaponReach(Situation situation)
        {
            if (situation.WeaponSetData == null || situation.NearestEnemy == null)
                return false;

            int altIdx = situation.CurrentWeaponSetIndex == 0 ? 1 : 0;
            if (altIdx >= situation.WeaponSetData.Length) return false;

            var altSet = situation.WeaponSetData[altIdx];
            if (!altSet.HasWeapons) return false;

            float enemyDist = situation.NearestEnemyDistance;

            // With an enemy adjacent, only a weapon that works next to enemies is worth switching to.
            if (IsEngaged(situation) && altSet.EngagedRating == 0)
            {
                Log.Planning.Info($"[{RoleName}] Weapon switch-after skipped — {altSet.PrimaryWeaponName} can't attack with an enemy adjacent");
                return false;
            }

            // 원거리 대체 무기: 사거리 내 확인
            if (altSet.HasRangedWeapon && altSet.PrimaryWeaponRange > 0)
            {
                if (enemyDist <= altSet.PrimaryWeaponRange)
                {
                    if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] CanAlternateWeaponReach: ranged alt weapon range={altSet.PrimaryWeaponRange:F0} >= enemy dist={enemyDist:F1}");
                    return true;
                }
            }

            // 근접 대체 무기: 근접 사거리 내 확인
            if (altSet.HasMeleeWeapon && enemyDist <= 3f)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] CanAlternateWeaponReach: melee alt weapon, enemy at {enemyDist:F1} tiles (within melee)");
                return true;
            }

            // MP 남으면 전환 후 이동 가능 → 도달 가능으로 간주
            float currentMP = CombatAPI.GetCurrentMP(situation.Unit);
            if (currentMP > 0)
            {
                if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] CanAlternateWeaponReach: MP={currentMP:F1} remaining, can move after switch");
                return true;
            }

            if (Main.IsDebugEnabled) Log.Planning.Debug($"[{RoleName}] CanAlternateWeaponReach: false — alt range={altSet.PrimaryWeaponRange:F0}, enemy={enemyDist:F1}, MP=0");
            return false;
        }

        #endregion
    }
}
