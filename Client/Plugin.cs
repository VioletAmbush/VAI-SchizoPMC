using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;

namespace SchizoPMC
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("EscapeFromTarkov.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public ConfigRepository? ConfigRepository { get; set; }

        public static Plugin? Instance { get; set; }
        private EventHandler<SettingChangedEventArgs>? _configSettingChangedHandler;
        private Harmony? _harmony;
        private bool _fatalErrorTriggered;

        public new ManualLogSource Logger => base.Logger;

        private void Awake()
        {
            Instance = this;
            try
            {
                ConfigRepository = new ConfigRepository(Config);

                _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
                Patches.Apply(_harmony);

                Logger.LogInfo($"[{PluginInfo.PLUGIN_NAME}] loaded!");
            }
            catch (Exception exception)
            {
                Logger.LogError($"[{PluginInfo.PLUGIN_NAME}] failed to initialize: {exception}");
                FailFast();
                throw;
            }
        }

        private void OnEnable()
        {
            if (ConfigRepository is null)
            {
                return;
            }

            _configSettingChangedHandler ??= (_, args) => ConfigRepository.UpdateValue(args);
            Config.SettingChanged += _configSettingChangedHandler;
        }

        private void OnDisable()
        {
            if (_configSettingChangedHandler is null)
            {
                return;
            }

            Config.SettingChanged -= _configSettingChangedHandler;
        }

        private void OnDestroy()
        {
            Instance = null;

            if (_configSettingChangedHandler is not null)
            {
                Config.SettingChanged -= _configSettingChangedHandler;
            }

            _harmony?.UnpatchSelf();
            _harmony = null;
            _configSettingChangedHandler = null;
            ConfigRepository = null;
        }

        internal void FailFast()
        {
            if (_fatalErrorTriggered)
            {
                return;
            }

            _fatalErrorTriggered = true;
            enabled = false;

            if (_configSettingChangedHandler is not null)
            {
                Config.SettingChanged -= _configSettingChangedHandler;
            }

            _harmony?.UnpatchSelf();
        }

        internal void FailFast(string message, Exception? exception = null)
        {
            if (exception == null)
            {
                Logger.LogError($"[{PluginInfo.PLUGIN_NAME}] {message}");
            }
            else
            {
                Logger.LogError($"[{PluginInfo.PLUGIN_NAME}] {message}: {exception}");
            }

            FailFast();
        }
    }
}

