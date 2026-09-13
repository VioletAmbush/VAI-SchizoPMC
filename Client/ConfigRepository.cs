using BepInEx.Configuration;
using System;
using System.Collections.Generic;

namespace SchizoPMC
{
    public class ConfigRepository
    {
        internal enum Section
        {
            GlobalSettings,
            TriggerSettings,
            CooldownSettings,
            IdleSettings,
            PhraseSettings,
        }

        internal enum ToggleKey
        {
            ModEnabled,
            AutoTalkPanelEnabled,
            HitCalloutEnabled,
            KillCalloutEnabled,
            IdleCalloutEnabled,
            RequireGameFocus,
            UseScavSpecificKillPhrase,
            IncludeIdleContextPhrases,
        }

        internal enum FloatStatKey
        {
            GlobalCooldownSeconds,
            SamePhraseCooldownSeconds,
            IdleMinQuietSeconds,
            IdleMaxQuietSeconds,
        }

        internal enum PhraseStatKey
        {
            HitEnemy,
            KillEnemy,
            KillScav,
            IdleBlabber,
            IdleGoing,
            IdleReady,
            IdleClear,
            IdleGoForward,
            IdleAttention,
            IdleGoGoGo,
            NeedWeapon,
            NeedAmmo,
            NeedMedkit,
            OnLoot,
            ExitLocated,
            OpenDoor,
            CheckHim,
            LootWeapon,
            Reloading,
            Bleeding,
            HandBroken,
            LegBroken,
            Dehydrated,
            Exhausted,
        }

        private const string KeyTemplate = "{0}-{1}";
        private readonly ConfigFile _config;

        private readonly Dictionary<string, bool> _boolValues = new Dictionary<string, bool>();
        private readonly Dictionary<string, float> _floatValues = new Dictionary<string, float>();
        private readonly Dictionary<string, string> _stringValues = new Dictionary<string, string>();

        internal bool this[ToggleKey key] => _boolValues[GetKey(GetSection(key), key.ToString())];
        internal float this[FloatStatKey key] => _floatValues[GetKey(GetSection(key), key.ToString())];
        internal string this[PhraseStatKey key] => _stringValues[GetKey(GetSection(key), key.ToString())];

        public ConfigRepository(ConfigFile configFile)
        {
            _config = configFile;

            BindConfig(Section.GlobalSettings, ToggleKey.ModEnabled, true, "Master switch for the entire mod");
            BindConfig(Section.GlobalSettings, ToggleKey.RequireGameFocus, true, "Only speak while the Tapkov game window is focused");

            BindConfig(Section.TriggerSettings, ToggleKey.AutoTalkPanelEnabled, true, "Enable context-aware auto-talk behavior");
            BindConfig(Section.TriggerSettings, ToggleKey.HitCalloutEnabled, true, "Trigger phrase after enemy hit");
            BindConfig(Section.TriggerSettings, ToggleKey.KillCalloutEnabled, true, "Trigger phrase after enemy kill");
            BindConfig(Section.TriggerSettings, ToggleKey.IdleCalloutEnabled, true, "Trigger idle/random phrases during long silence");
            BindConfig(Section.TriggerSettings, ToggleKey.UseScavSpecificKillPhrase, true, "Use scav-specific phrase when killed enemy is a scav");
            BindConfig(Section.TriggerSettings, ToggleKey.IncludeIdleContextPhrases, true, "Allow context phrases (need ammo/medkit/weapon/on loot) in idle pool");

            BindConfig(Section.CooldownSettings, FloatStatKey.GlobalCooldownSeconds, 5f, "Minimum time between any two phrases");
            BindConfig(Section.CooldownSettings, FloatStatKey.SamePhraseCooldownSeconds, 20f, "Minimum time before repeating the same phrase");

            BindConfig(Section.IdleSettings, FloatStatKey.IdleMinQuietSeconds, 10f, "Minimum silent time before idle phrase");
            BindConfig(Section.IdleSettings, FloatStatKey.IdleMaxQuietSeconds, 20f, "Maximum silent time before idle phrase");

            BindConfig(Section.PhraseSettings, PhraseStatKey.HitEnemy, "EnemyHit", "Phrase trigger for enemy hit");
            BindConfig(Section.PhraseSettings, PhraseStatKey.KillEnemy, "EnemyDown", "Phrase trigger for non-scav kill");
            BindConfig(Section.PhraseSettings, PhraseStatKey.KillScav, "ScavDown", "Phrase trigger for scav kill");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleBlabber, "MumblePhrase", "Idle blabber phrase (F1 equivalent)");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleGoing, "Going", "Idle phrase option");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleReady, "Ready", "Idle phrase option");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleClear, "Clear", "Idle phrase option");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleGoForward, "GoForward", "Idle phrase option");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleAttention, "Roger", "Idle phrase option");
            BindConfig(Section.PhraseSettings, PhraseStatKey.IdleGoGoGo, "FollowMe", "Idle phrase option (Go go go)");
            BindConfig(Section.PhraseSettings, PhraseStatKey.NeedWeapon, "NeedWeapon", "Context phrase when no usable weapon is equipped");
            BindConfig(Section.PhraseSettings, PhraseStatKey.NeedAmmo, "NeedAmmo", "Context phrase when weapon has no ammo");
            BindConfig(Section.PhraseSettings, PhraseStatKey.NeedMedkit, "NeedMedkit", "Context phrase when health is below threshold");
            BindConfig(Section.PhraseSettings, PhraseStatKey.OnLoot, "OnLoot", "Context phrase while looting");
            BindConfig(Section.PhraseSettings, PhraseStatKey.ExitLocated, "ExitLocated", "Context phrase while looking at an extract");
            BindConfig(Section.PhraseSettings, PhraseStatKey.OpenDoor, "OpenDoor", "Context phrase while looking at a door");
            BindConfig(Section.PhraseSettings, PhraseStatKey.CheckHim, "CheckHim", "Context phrase while looking at a corpse");
            BindConfig(Section.PhraseSettings, PhraseStatKey.LootWeapon, "LootWeapon", "Context phrase while looking at a weapon on the ground");
            BindConfig(Section.PhraseSettings, PhraseStatKey.Reloading, "OnWeaponReload", "Context phrase while reloading");
            BindConfig(Section.PhraseSettings, PhraseStatKey.Bleeding, "Bleeding", "Context phrase while bleeding");
            BindConfig(Section.PhraseSettings, PhraseStatKey.HandBroken, "HandBroken", "Context phrase while an arm is fractured");
            BindConfig(Section.PhraseSettings, PhraseStatKey.LegBroken, "LegBroken", "Context phrase while a leg is fractured");
            BindConfig(Section.PhraseSettings, PhraseStatKey.Dehydrated, "Dehydrated", "Context phrase while hydration is low");
            BindConfig(Section.PhraseSettings, PhraseStatKey.Exhausted, "Exhausted", "Context phrase while energy is low");
        }

        public void UpdateValue(SettingChangedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            var key = GetKey(args.ChangedSetting.Definition.Section, args.ChangedSetting.Definition.Key);

            if (args.ChangedSetting.BoxedValue is bool boolVal)
            {
                _boolValues[key] = boolVal;
                return;
            }

            if (args.ChangedSetting.BoxedValue is float floatVal)
            {
                _floatValues[key] = floatVal;
                return;
            }

            if (args.ChangedSetting.BoxedValue is int intVal)
            {
                _floatValues[key] = intVal;
                return;
            }

            if (args.ChangedSetting.BoxedValue is string stringVal)
            {
                _stringValues[key] = stringVal;
            }
        }

        private void BindConfig(Section section, ToggleKey key, bool def, string description)
        {
            var entry = _config.Bind(
                new ConfigDefinition(SectionToString(section), key.ToString()),
                def,
                new ConfigDescription(description));

            _boolValues[GetKey(section, key.ToString())] = entry.Value;
        }

        private void BindConfig(Section section, FloatStatKey key, float def, string description)
        {
            var entry = _config.Bind(
                new ConfigDefinition(SectionToString(section), key.ToString()),
                def,
                new ConfigDescription(description));

            _floatValues[GetKey(section, key.ToString())] = entry.Value;
        }

        private void BindConfig(Section section, PhraseStatKey key, string def, string description)
        {
            var entry = _config.Bind(
                new ConfigDefinition(SectionToString(section), key.ToString()),
                def,
                new ConfigDescription(description));

            _stringValues[GetKey(section, key.ToString())] = entry.Value;
        }

        private static Section GetSection(ToggleKey key)
        {
            switch (key)
            {
                case ToggleKey.ModEnabled:
                case ToggleKey.RequireGameFocus:
                    return Section.GlobalSettings;

                default:
                    return Section.TriggerSettings;
            }
        }

        private static Section GetSection(FloatStatKey key)
        {
            switch (key)
            {
                case FloatStatKey.IdleMinQuietSeconds:
                case FloatStatKey.IdleMaxQuietSeconds:
                    return Section.IdleSettings;

                default:
                    return Section.CooldownSettings;
            }
        }

        private static Section GetSection(PhraseStatKey _)
        {
            return Section.PhraseSettings;
        }

        private static string GetKey(Section section, string key)
        {
            return string.Format(KeyTemplate, SectionToString(section), key);
        }

        private static string GetKey(string section, string key)
        {
            return string.Format(KeyTemplate, section, key);
        }

        private static string SectionToString(Section section)
        {
            switch (section)
            {
                case Section.GlobalSettings:
                    return "Global Settings";

                case Section.TriggerSettings:
                    return "Trigger Settings";

                case Section.CooldownSettings:
                    return "Cooldown Settings";

                case Section.IdleSettings:
                    return "Idle Settings";

                case Section.PhraseSettings:
                    return "Phrase Settings";

                default:
                    return "Unknown Section";
            }
        }
    }
}

