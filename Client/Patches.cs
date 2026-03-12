using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using static SchizoPMC.ConfigRepository;

namespace SchizoPMC
{
    internal static class Patches
    {
        private const float SchedulerTickSeconds = 0.2f;
        private const float HitDedupeSeconds = 0.25f;
        private const float HitConfirmDelaySeconds = 0.2f;
        private const float KillDedupeSeconds = 0.4f;
        private const float HurtPriorityThreshold = 0.7f;
        private const float LowMetabolismThreshold = 0.3f;
        private static readonly System.Random Random = new System.Random();
        private static readonly Dictionary<EPhraseTrigger, float> LastPhraseTimes = new Dictionary<EPhraseTrigger, float>();
        private static readonly HashSet<EPhraseTrigger> ActiveQuickCommands = new HashSet<EPhraseTrigger>();
        private static readonly EBodyPart[] BodyParts =
            Enum.GetValues(typeof(EBodyPart)) is EBodyPart[] values
                ? Array.FindAll(values, part => part != EBodyPart.Common)
                : throw new InvalidOperationException("Failed to resolve EBodyPart values.");

        private static readonly EquipmentSlot[] WeaponSlots =
        {
            EquipmentSlot.FirstPrimaryWeapon,
            EquipmentSlot.SecondPrimaryWeapon,
            EquipmentSlot.Holster,
        };

        private static readonly PhraseStatKey[] IdlePhraseKeys =
        {
            PhraseStatKey.IdleBlabber,
            PhraseStatKey.IdleGoing,
            PhraseStatKey.IdleReady,
            PhraseStatKey.IdleClear,
            PhraseStatKey.IdleGoForward,
            PhraseStatKey.IdleAttention,
            PhraseStatKey.IdleGoGoGo,
        };

        private static Player? _localPlayer;
        private static float _nextTickAt = float.NegativeInfinity;
        private static float _nextIdleAt = float.NegativeInfinity;
        private static float _lastPhraseAt = float.NegativeInfinity;
        private static float _lastHitAt = float.NegativeInfinity;
        private static float _lastKillAt = float.NegativeInfinity;
        private static Player? _pendingHitAggressor;
        private static EPhraseTrigger _pendingHitTrigger = EPhraseTrigger.None;
        private static float _pendingHitAt = float.NegativeInfinity;
        private static bool _lootSessionActive;

        private static ConfigRepository Config
        {
            get
            {
                Plugin plugin = Plugin.Instance ?? throw new InvalidOperationException("Plugin instance is not available.");
                return plugin.ConfigRepository ?? throw new InvalidOperationException("Config repository is not initialized.");
            }
        }

        public static void Apply(Harmony harmony)
        {
            if (harmony == null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            ResetState();

            PatchRequired(harmony, AccessTools.Method(typeof(Player), "LateUpdate"), nameof(LateUpdatePostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(typeof(Player), "ApplyDamageInfo", new[] { typeof(DamageInfoStruct), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) }), nameof(ApplyDamageInfoPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(typeof(Player), "OnBeenKilledByAggressor", new[] { typeof(IPlayer), typeof(DamageInfoStruct), typeof(EBodyPart), typeof(EDamageType) }), nameof(OnBeenKilledByAggressorPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(typeof(Player), "InventoryOpenRaiseAction", new[] { typeof(bool) }), nameof(InventoryOpenRaiseActionPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(typeof(Player), "TryInteractionCallback", new[] { typeof(EFT.Interactive.LootableContainer) }), nameof(TryInteractionCallbackPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(typeof(EFT.GamePlayerOwner), "ShowInventoryScreenLoot", new[] { typeof(CompoundItem), typeof(Action), typeof(bool) }), nameof(ShowInventoryScreenLootPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(AccessTools.TypeByName("EFT.LocalGame"), "CleanUp"), nameof(LocalGameCleanUpPrefix), asPrefix: true);

            Type gesturesQuickPanelType = AccessTools.TypeByName("EFT.UI.Gestures.GesturesQuickPanel")
                ?? throw new InvalidOperationException("Required type 'EFT.UI.Gestures.GesturesQuickPanel' was not found.");

            PatchRequired(harmony, AccessTools.Method(gesturesQuickPanelType, "Show", new[] { typeof(EFT.GamePlayerOwner) }), nameof(GesturesQuickPanelShowPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(gesturesQuickPanelType, "AddQuickCommand", new[] { typeof(EPhraseTrigger) }), nameof(AddQuickCommandPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(gesturesQuickPanelType, "RemoveQuickCommand", new[] { typeof(EPhraseTrigger) }), nameof(RemoveQuickCommandPostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(gesturesQuickPanelType, "Close"), nameof(GesturesQuickPanelClosePostfix), asPrefix: false);
            PatchRequired(harmony, AccessTools.Method(gesturesQuickPanelType, "OnDestroy"), nameof(GesturesQuickPanelOnDestroyPostfix), asPrefix: false);
        }

        private static void PatchRequired(Harmony harmony, MethodInfo? targetMethod, string patchMethodName, bool asPrefix)
        {
            if (targetMethod == null)
            {
                throw new InvalidOperationException($"Required method missing for patch '{patchMethodName}'.");
            }

            MethodInfo? patchMethod = AccessTools.Method(typeof(Patches), patchMethodName);
            if (patchMethod == null)
            {
                throw new InvalidOperationException($"Patch method '{patchMethodName}' was not found.");
            }

            HarmonyMethod harmonyMethod = new HarmonyMethod(patchMethod);
            if (asPrefix)
            {
                harmony.Patch(targetMethod, prefix: harmonyMethod);
            }
            else
            {
                harmony.Patch(targetMethod, postfix: harmonyMethod);
            }
        }

        private static void LocalGameCleanUpPrefix()
        {
            try
            {
                ResetState();
            }
            catch (Exception ex)
            {
                Fatal("LocalGame cleanup patch failed", ex);
            }
        }

        private static void LateUpdatePostfix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance.IsAI || !__instance.IsYourPlayer)
                {
                    return;
                }

                if (!Config[ToggleKey.ModEnabled])
                {
                    return;
                }

                float now = Time.time;
                EnsureLocalPlayer(__instance, now);

                if (!CanRunForPlayer(__instance))
                {
                    return;
                }

                if (now < _nextTickAt)
                {
                    return;
                }

                _nextTickAt = now + SchedulerTickSeconds;

                ProcessPendingHit(now);
                ProcessIdlePhrase(__instance, now);
            }
            catch (Exception ex)
            {
                Fatal("LateUpdate patch failed", ex);
            }
        }

        private static void ApplyDamageInfoPostfix(Player __instance, DamageInfoStruct __0)
        {
            try
            {
                if (!Config[ToggleKey.ModEnabled] || !Config[ToggleKey.HitCalloutEnabled])
                {
                    return;
                }

                if (!TryResolveLocalAggressor(__0, out Player aggressor))
                {
                    return;
                }

                if (__instance == null || __instance.IsYourPlayer || ReferenceEquals(__instance, aggressor))
                {
                    return;
                }

                if (!CanRunForPlayer(aggressor))
                {
                    return;
                }

                if (!IsCombatTarget(aggressor, __instance))
                {
                    return;
                }

                float now = Time.time;
                if (now - _lastHitAt < HitDedupeSeconds)
                {
                    return;
                }

                _lastHitAt = now;
                EPhraseTrigger trigger = GetConfiguredTrigger(PhraseStatKey.HitEnemy);
                SchedulePendingHit(aggressor, trigger, now);
            }
            catch (Exception ex)
            {
                Fatal("ApplyDamageInfo patch failed", ex);
            }
        }

        private static void OnBeenKilledByAggressorPostfix(Player __instance, IPlayer __0, DamageInfoStruct __1, EBodyPart __2, EDamageType __3)
        {
            try
            {
                if (!Config[ToggleKey.ModEnabled] || !Config[ToggleKey.KillCalloutEnabled])
                {
                    return;
                }

                if (__0 == null || !__0.IsYourPlayer)
                {
                    return;
                }

                Player aggressor = __0 as Player ?? throw new InvalidOperationException("Local aggressor is not EFT.Player.");
                if (__instance == null || __instance.IsYourPlayer || ReferenceEquals(__instance, aggressor))
                {
                    return;
                }

                if (!CanRunForPlayer(aggressor))
                {
                    return;
                }

                if (!IsCombatTarget(aggressor, __instance))
                {
                    return;
                }

                ClearPendingHit();

                float now = Time.time;
                if (now - _lastKillAt < KillDedupeSeconds)
                {
                    return;
                }

                _lastKillAt = now;
                PhraseStatKey phraseKey = Config[ToggleKey.UseScavSpecificKillPhrase] && __instance.Side == EPlayerSide.Savage
                    ? PhraseStatKey.KillScav
                    : PhraseStatKey.KillEnemy;

                EPhraseTrigger trigger = GetConfiguredTrigger(phraseKey);
                TrySpeak(aggressor, trigger, now, ignoreGlobalCooldown: true);
            }
            catch (Exception ex)
            {
                Fatal("OnBeenKilledByAggressor patch failed", ex);
            }
        }

        private static void InventoryOpenRaiseActionPostfix(Player __instance, bool __0)
        {
            try
            {
                if (__instance == null || __instance.IsAI || !__instance.IsYourPlayer)
                {
                    return;
                }

                if (!__0)
                {
                    _lootSessionActive = false;
                    return;
                }

                if (IsLootInteractable(__instance.InteractableObject))
                {
                    _lootSessionActive = true;
                }
            }
            catch (Exception ex)
            {
                Fatal("InventoryOpenRaiseAction patch failed", ex);
            }
        }

        private static void TryInteractionCallbackPostfix(Player __instance, EFT.Interactive.LootableContainer __0)
        {
            try
            {
                if (__instance == null || __instance.IsAI || !__instance.IsYourPlayer)
                {
                    return;
                }

                _lootSessionActive = true;
            }
            catch (Exception ex)
            {
                Fatal("TryInteractionCallback patch failed", ex);
            }
        }

        private static void ShowInventoryScreenLootPostfix(CompoundItem __0)
        {
            try
            {
                if (__0 == null)
                {
                    return;
                }

                Player? player = EFT.GamePlayerOwner.MyPlayer;
                if (player == null || player.IsAI || !player.IsYourPlayer)
                {
                    return;
                }

                _lootSessionActive = true;
            }
            catch (Exception ex)
            {
                Fatal("GamePlayerOwner.ShowInventoryScreenLoot patch failed", ex);
            }
        }

        private static void EnsureLocalPlayer(Player player, float now)
        {
            if (ReferenceEquals(_localPlayer, player))
            {
                return;
            }

            _localPlayer = player;
            _nextTickAt = now;
            _nextIdleAt = float.NegativeInfinity;
            _lastPhraseAt = float.NegativeInfinity;
            _lastHitAt = float.NegativeInfinity;
            _lastKillAt = float.NegativeInfinity;
            ClearPendingHit();
            _lootSessionActive = false;
            LastPhraseTimes.Clear();
            ActiveQuickCommands.Clear();
            ScheduleNextIdle(now);
        }

        private static void ProcessPendingHit(float now)
        {
            if (_pendingHitTrigger == EPhraseTrigger.None || float.IsNegativeInfinity(_pendingHitAt))
            {
                return;
            }

            if (now - _pendingHitAt < HitConfirmDelaySeconds)
            {
                return;
            }

            Player? aggressor = _pendingHitAggressor;
            EPhraseTrigger trigger = _pendingHitTrigger;
            ClearPendingHit();

            if (aggressor == null)
            {
                return;
            }

            TrySpeak(aggressor, trigger, now, ignoreGlobalCooldown: true);
        }

        private static void SchedulePendingHit(Player aggressor, EPhraseTrigger trigger, float now)
        {
            _pendingHitAggressor = aggressor;
            _pendingHitTrigger = trigger;
            _pendingHitAt = now;
        }

        private static void ClearPendingHit()
        {
            _pendingHitAggressor = null;
            _pendingHitTrigger = EPhraseTrigger.None;
            _pendingHitAt = float.NegativeInfinity;
        }

        private static void ProcessIdlePhrase(Player player, float now)
        {
            if (!Config[ToggleKey.IdleCalloutEnabled])
            {
                return;
            }

            if (_nextIdleAt <= 0f || float.IsNegativeInfinity(_nextIdleAt))
            {
                ScheduleNextIdle(now);
                return;
            }

            if (now < _nextIdleAt)
            {
                return;
            }

            EPhraseTrigger trigger = ChooseIdleTrigger(player);
            TrySpeak(player, trigger, now, ignoreGlobalCooldown: false);
            ScheduleNextIdle(now);
        }

        private static void ScheduleNextIdle(float now)
        {
            float minSeconds = Config[FloatStatKey.IdleMinQuietSeconds];
            float maxSeconds = Config[FloatStatKey.IdleMaxQuietSeconds];

            if (minSeconds <= 0f)
            {
                throw new InvalidOperationException("IdleMinQuietSeconds must be greater than 0.");
            }

            if (maxSeconds < minSeconds)
            {
                throw new InvalidOperationException("IdleMaxQuietSeconds must be greater than or equal to IdleMinQuietSeconds.");
            }

            float range = maxSeconds - minSeconds;
            float offset = range <= 0f ? 0f : (float)(Random.NextDouble() * range);
            _nextIdleAt = now + minSeconds + offset;
        }

        private static bool TryResolveLocalAggressor(DamageInfoStruct damageInfo, out Player aggressor)
        {
            aggressor = null!;

            IPlayerOwner? owner = damageInfo.Player;
            if (owner?.iPlayer is Player ownerPlayer && ownerPlayer.IsYourPlayer)
            {
                aggressor = ownerPlayer;
                return true;
            }

            if (_localPlayer == null)
            {
                return false;
            }

            if (string.IsNullOrEmpty(_localPlayer.ProfileId) || string.IsNullOrEmpty(damageInfo.SourceId))
            {
                return false;
            }

            if (!string.Equals(_localPlayer.ProfileId, damageInfo.SourceId, StringComparison.Ordinal))
            {
                return false;
            }

            aggressor = _localPlayer;
            return true;
        }

        private static EPhraseTrigger ChooseIdleTrigger(Player player)
        {
            if (TryResolvePriorityIdleTrigger(player, out EPhraseTrigger trigger))
            {
                return trigger;
            }

            List<EPhraseTrigger> pool = new List<EPhraseTrigger>(12);

            for (int i = 0; i < IdlePhraseKeys.Length; i++)
            {
                pool.Add(GetConfiguredTrigger(IdlePhraseKeys[i]));
            }

            if (Config[ToggleKey.IncludeIdleContextPhrases])
            {
                if (IsHealthLow(player))
                {
                    pool.Add(GetConfiguredTrigger(PhraseStatKey.NeedMedkit));
                }

                if (IsCurrentWeaponOutOfAmmo(player))
                {
                    pool.Add(GetConfiguredTrigger(PhraseStatKey.NeedAmmo));
                }

                if (HasNoWeaponEquipped(player))
                {
                    pool.Add(GetConfiguredTrigger(PhraseStatKey.NeedWeapon));
                }

                if (IsLooting(player))
                {
                    pool.Add(GetConfiguredTrigger(PhraseStatKey.OnLoot));
                }
            }

            if (pool.Count == 0)
            {
                throw new InvalidOperationException("Idle phrase pool is empty.");
            }

            int index = Random.Next(pool.Count);
            return pool[index];
        }

        private static bool TryResolvePriorityIdleTrigger(Player player, out EPhraseTrigger trigger)
        {
            if (HasActiveBleeding(player) || ActiveQuickCommands.Contains(EPhraseTrigger.Bleeding))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.Bleeding);
                return true;
            }

            if (TryResolveFractureTrigger(player, out trigger))
            {
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.HandBroken))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.HandBroken);
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.LegBroken))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.LegBroken);
                return true;
            }

            if (IsEnergyLow(player) || ActiveQuickCommands.Contains(EPhraseTrigger.Exhausted))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.Exhausted);
                return true;
            }

            if (IsHydrationLow(player) || ActiveQuickCommands.Contains(EPhraseTrigger.Dehydrated))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.Dehydrated);
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.NeedMedkit))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.NeedMedkit);
                return true;
            }

            if (TryResolveHurtPriorityTrigger(player, out trigger))
            {
                return true;
            }

            if (IsLooting(player))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.OnLoot);
                return true;
            }

            if (HasNoWeaponEquipped(player) || ActiveQuickCommands.Contains(EPhraseTrigger.NeedWeapon))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.NeedWeapon);
                return true;
            }

            if (IsCurrentWeaponOutOfAmmo(player) || ActiveQuickCommands.Contains(EPhraseTrigger.NeedAmmo) || ActiveQuickCommands.Contains(EPhraseTrigger.OnOutOfAmmo))
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.NeedAmmo);
                return true;
            }

            trigger = default;
            return false;
        }

        private static bool TryResolveHurtPriorityTrigger(Player player, out EPhraseTrigger trigger)
        {
            if (!TryGetHealthRatio(player, out float healthRatio) || healthRatio >= HurtPriorityThreshold)
            {
                trigger = default;
                return false;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.HurtNearDeath))
            {
                trigger = EPhraseTrigger.HurtNearDeath;
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.HurtHeavy))
            {
                trigger = EPhraseTrigger.HurtHeavy;
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.HurtMedium))
            {
                trigger = EPhraseTrigger.HurtMedium;
                return true;
            }

            if (ActiveQuickCommands.Contains(EPhraseTrigger.HurtLight))
            {
                trigger = EPhraseTrigger.HurtLight;
                return true;
            }

            trigger = EPhraseTrigger.HurtLight;
            return true;
        }

        private static bool TrySpeak(Player player, EPhraseTrigger trigger, float now, bool ignoreGlobalCooldown)
        {
            if (!CanRunForPlayer(player))
            {
                return false;
            }

            if (trigger == EPhraseTrigger.None || trigger == EPhraseTrigger.PhraseNone)
            {
                throw new InvalidOperationException($"Invalid phrase trigger '{trigger}'.");
            }

            float globalCooldown = Config[FloatStatKey.GlobalCooldownSeconds];
            float samePhraseCooldown = Config[FloatStatKey.SamePhraseCooldownSeconds];

            if (globalCooldown < 0f)
            {
                throw new InvalidOperationException("GlobalCooldownSeconds cannot be negative.");
            }

            if (samePhraseCooldown < 0f)
            {
                throw new InvalidOperationException("SamePhraseCooldownSeconds cannot be negative.");
            }

            if (!ignoreGlobalCooldown && now - _lastPhraseAt < globalCooldown)
            {
                return false;
            }

            if (LastPhraseTimes.TryGetValue(trigger, out float lastTriggerAt) && now - lastTriggerAt < samePhraseCooldown)
            {
                return false;
            }

            player.Say(trigger, true);
            _lastPhraseAt = now;
            LastPhraseTimes[trigger] = now;
            return true;
        }

        private static EPhraseTrigger GetConfiguredTrigger(PhraseStatKey key)
        {
            string configuredValue = Config[key];
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                throw new InvalidOperationException($"Phrase setting '{key}' is empty.");
            }

            if (!Enum.TryParse(configuredValue, true, out EPhraseTrigger trigger))
            {
                throw new InvalidOperationException($"Phrase setting '{key}' has invalid EPhraseTrigger value '{configuredValue}'.");
            }

            if (trigger == EPhraseTrigger.None || trigger == EPhraseTrigger.PhraseNone)
            {
                throw new InvalidOperationException($"Phrase setting '{key}' cannot be '{trigger}'.");
            }

            return trigger;
        }

        private static bool CanRunForPlayer(Player player)
        {
            if (player == null)
            {
                return false;
            }

            if (Config[ToggleKey.RequireGameFocus] && !Application.isFocused)
            {
                return false;
            }

            IHealthController? healthController = player.HealthController;
            if (healthController == null || !healthController.IsAlive)
            {
                return false;
            }

            return true;
        }

        private static bool IsCombatTarget(Player aggressor, Player target)
        {
            if (target.IsYourPlayer || ReferenceEquals(aggressor, target))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(aggressor.ProfileId) && aggressor.ProfileId == target.ProfileId)
            {
                return false;
            }

            return true;
        }

        private static bool IsLooting(Player player)
        {
            if (!player.IsInventoryOpened)
            {
                _lootSessionActive = false;
                return false;
            }

            if (IsLootInteractable(player.InteractableObject))
            {
                _lootSessionActive = true;
                return true;
            }

            return _lootSessionActive;
        }

        private static bool IsLootInteractable(object? interactableObject)
        {
            if (interactableObject == null)
            {
                return false;
            }

            if (interactableObject is EFT.Interactive.LootableContainer || interactableObject is EFT.Interactive.Corpse)
            {
                return true;
            }

            string typeName = interactableObject.GetType().Name;
            return typeName.IndexOf("LootItem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void GesturesQuickPanelShowPostfix(EFT.GamePlayerOwner __0)
        {
            try
            {
                Player? player = EFT.GamePlayerOwner.MyPlayer;
                if (player == null || player.IsAI || !player.IsYourPlayer)
                {
                    return;
                }

                EnsureLocalPlayer(player, Time.time);
                ActiveQuickCommands.Clear();
            }
            catch (Exception ex)
            {
                Fatal("GesturesQuickPanel.Show patch failed", ex);
            }
        }

        private static void AddQuickCommandPostfix(EPhraseTrigger __0)
        {
            try
            {
                if (!Config[ToggleKey.ModEnabled] || !Config[ToggleKey.AutoTalkPanelEnabled])
                {
                    return;
                }

                if (__0 == EPhraseTrigger.None || __0 == EPhraseTrigger.PhraseNone)
                {
                    return;
                }

                if (!ActiveQuickCommands.Add(__0))
                {
                    return;
                }

                Player? player = _localPlayer;
                if (player == null || !CanRunForPlayer(player))
                {
                    return;
                }

                if (TryResolveQuickCommandPhraseKey(player, __0, out PhraseStatKey key))
                {
                    TrySpeakFromEvent(key);
                    return;
                }

                if (!TryResolveQuickCommandDirectTrigger(__0, out EPhraseTrigger directTrigger))
                {
                    return;
                }

                TrySpeak(player, directTrigger, Time.time, ignoreGlobalCooldown: true);
            }
            catch (Exception ex)
            {
                Fatal("GesturesQuickPanel.AddQuickCommand patch failed", ex);
            }
        }

        private static void RemoveQuickCommandPostfix(EPhraseTrigger __0)
        {
            try
            {
                if (__0 == EPhraseTrigger.None || __0 == EPhraseTrigger.PhraseNone)
                {
                    return;
                }

                ActiveQuickCommands.Remove(__0);
            }
            catch (Exception ex)
            {
                Fatal("GesturesQuickPanel.RemoveQuickCommand patch failed", ex);
            }
        }

        private static void GesturesQuickPanelClosePostfix()
        {
            try
            {
                ActiveQuickCommands.Clear();
            }
            catch (Exception ex)
            {
                Fatal("GesturesQuickPanel.Close patch failed", ex);
            }
        }

        private static void GesturesQuickPanelOnDestroyPostfix()
        {
            try
            {
                ActiveQuickCommands.Clear();
            }
            catch (Exception ex)
            {
                Fatal("GesturesQuickPanel.OnDestroy patch failed", ex);
            }
        }

        private static void TrySpeakFromEvent(PhraseStatKey key)
        {
            if (!Config[ToggleKey.ModEnabled] || !Config[ToggleKey.AutoTalkPanelEnabled])
            {
                return;
            }

            Player? player = _localPlayer;
            if (player == null || !CanRunForPlayer(player))
            {
                return;
            }

            EPhraseTrigger trigger = GetConfiguredTrigger(key);
            TrySpeak(player, trigger, Time.time, ignoreGlobalCooldown: true);
        }

        private static bool TryResolveQuickCommandPhraseKey(Player player, EPhraseTrigger trigger, out PhraseStatKey key)
        {
            switch (trigger)
            {
                case EPhraseTrigger.OpenDoor:
                    key = PhraseStatKey.OpenDoor;
                    return true;

                case EPhraseTrigger.CheckHim:
                    key = PhraseStatKey.CheckHim;
                    return true;

                case EPhraseTrigger.LootWeapon:
                    key = PhraseStatKey.LootWeapon;
                    return true;

                case EPhraseTrigger.OnWeaponReload:
                    key = PhraseStatKey.Reloading;
                    return true;

                case EPhraseTrigger.Bleeding:
                    key = PhraseStatKey.Bleeding;
                    return true;

                case EPhraseTrigger.HandBroken:
                    key = PhraseStatKey.HandBroken;
                    return true;

                case EPhraseTrigger.LegBroken:
                    key = PhraseStatKey.LegBroken;
                    return true;

                case EPhraseTrigger.Dehydrated:
                    key = PhraseStatKey.Dehydrated;
                    return true;

                case EPhraseTrigger.Exhausted:
                    key = PhraseStatKey.Exhausted;
                    return true;

                case EPhraseTrigger.ExitLocated:
                    key = PhraseStatKey.ExitLocated;
                    return true;

                case EPhraseTrigger.NeedAmmo:
                case EPhraseTrigger.OnOutOfAmmo:
                    key = PhraseStatKey.NeedAmmo;
                    return true;

                case EPhraseTrigger.NeedMedkit:
                    key = PhraseStatKey.NeedMedkit;
                    return true;

                case EPhraseTrigger.NeedWeapon:
                    key = PhraseStatKey.NeedWeapon;
                    return true;

                case EPhraseTrigger.LootBody:
                case EPhraseTrigger.OnLoot:
                case EPhraseTrigger.LootContainer:
                case EPhraseTrigger.LootGeneric:
                    return TryResolveLootPhraseKey(player, out key);

                default:
                    key = default;
                    return false;
            }
        }

        private static bool TryResolveLootPhraseKey(Player player, out PhraseStatKey key)
        {
            EFT.Interactive.InteractableObject? interactable = player.InteractableObject;
            if (interactable is EFT.Interactive.Corpse corpse && corpse.Item != null)
            {
                key = PhraseStatKey.CheckHim;
                return true;
            }

            if (interactable is EFT.Interactive.LootItem lootItem)
            {
                key = lootItem.Item is Weapon ? PhraseStatKey.LootWeapon : PhraseStatKey.OnLoot;
                return true;
            }

            key = PhraseStatKey.OnLoot;
            return true;
        }

        private static bool TryResolveQuickCommandDirectTrigger(EPhraseTrigger trigger, out EPhraseTrigger directTrigger)
        {
            switch (trigger)
            {
                case EPhraseTrigger.LootKey:
                case EPhraseTrigger.LootMoney:
                case EPhraseTrigger.LootNothing:
                    directTrigger = trigger;
                    return true;

                default:
                    directTrigger = default;
                    return false;
            }
        }

        private static bool HasActiveBleeding(Player player)
        {
            IHealthController? healthController = player.HealthController;
            if (healthController == null)
            {
                return false;
            }

            for (int i = 0; i < BodyParts.Length; i++)
            {
                foreach (IEffect effect in healthController.GetAllActiveEffects(BodyParts[i]))
                {
                    string effectName = effect.GetType().Name;
                    if (effectName.IndexOf("Bleeding", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryResolveFractureTrigger(Player player, out EPhraseTrigger trigger)
        {
            IHealthController? healthController = player.HealthController;
            if (healthController == null)
            {
                trigger = default;
                return false;
            }

            bool armBroken = false;
            bool nonArmBroken = false;

            for (int i = 0; i < BodyParts.Length; i++)
            {
                EBodyPart bodyPart = BodyParts[i];
                if (!healthController.IsBodyPartBroken(bodyPart))
                {
                    continue;
                }

                if (bodyPart == EBodyPart.LeftArm || bodyPart == EBodyPart.RightArm)
                {
                    armBroken = true;
                }
                else
                {
                    nonArmBroken = true;
                }
            }

            if (armBroken)
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.HandBroken);
                return true;
            }

            if (nonArmBroken)
            {
                trigger = GetConfiguredTrigger(PhraseStatKey.LegBroken);
                return true;
            }

            trigger = default;
            return false;
        }

        private static bool IsHydrationLow(Player player)
        {
            IHealthController? activeHealthController = player.ActiveHealthController;
            if (activeHealthController == null)
            {
                return false;
            }

            return TryGetResourceRatio(activeHealthController.Hydration, out float ratio) && ratio < LowMetabolismThreshold;
        }

        private static bool IsEnergyLow(Player player)
        {
            IHealthController? activeHealthController = player.ActiveHealthController;
            if (activeHealthController == null)
            {
                return false;
            }

            return TryGetResourceRatio(activeHealthController.Energy, out float ratio) && ratio < LowMetabolismThreshold;
        }

        private static bool TryGetResourceRatio(ValueStruct resource, out float ratio)
        {
            ratio = 1f;
            if (resource.Maximum <= 0f)
            {
                return false;
            }

            ratio = resource.Current / resource.Maximum;
            return true;
        }

        private static bool IsHealthLow(Player player)
        {
            return TryGetHealthRatio(player, out float ratio) && ratio < 0.5f;
        }

        private static bool TryGetHealthRatio(Player player, out float ratio)
        {
            ratio = 1f;

            if (player.HealthController == null)
            {
                return false;
            }

            float current = 0f;
            float maximum = 0f;

            for (int i = 0; i < BodyParts.Length; i++)
            {
                EBodyPart bodyPart = BodyParts[i];
                ValueStruct hp = player.HealthController.GetBodyPartHealth(bodyPart);

                current += hp.Current;
                maximum += hp.Maximum;
            }

            if (maximum <= 0f)
            {
                return false;
            }

            ratio = current / maximum;
            return true;
        }

        private static bool HasNoWeaponEquipped(Player player)
        {
            if (player.HandsController?.Item is Weapon)
            {
                return false;
            }

            IEnumerable<Item> equippedItems = player.Inventory.GetItemsInSlots(WeaponSlots);
            foreach (Item item in equippedItems)
            {
                if (item is Weapon)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCurrentWeaponOutOfAmmo(Player player)
        {
            if (player.HandsController?.Item is not Weapon weapon)
            {
                return false;
            }

            if (weapon.GetCurrentMagazineCount() > 0)
            {
                return false;
            }

            if (weapon.ChamberAmmoCount > 0)
            {
                return false;
            }

            if (weapon.GetShellsInWeaponCount() > 0)
            {
                return false;
            }

            return true;
        }

        private static void ResetState()
        {
            _localPlayer = null;
            _nextTickAt = float.NegativeInfinity;
            _nextIdleAt = float.NegativeInfinity;
            _lastPhraseAt = float.NegativeInfinity;
            _lastHitAt = float.NegativeInfinity;
            _lastKillAt = float.NegativeInfinity;
            ClearPendingHit();
            _lootSessionActive = false;
            LastPhraseTimes.Clear();
            ActiveQuickCommands.Clear();
        }

        private static void LogInfo(string message)
        {
            Plugin.Instance?.Logger.LogInfo(message);
        }

        private static void Fatal(string message, Exception? exception)
        {
            Plugin? plugin = Plugin.Instance;
            if (plugin == null)
            {
                throw exception == null
                    ? new InvalidOperationException(message)
                    : new InvalidOperationException(message, exception);
            }

            plugin.FailFast(message, exception);
            throw exception == null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, exception);
        }
    }
}
