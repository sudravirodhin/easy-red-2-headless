using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace EasyRed2Headless
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.antigravity.easyred2.headless";
        public const string PluginName = "Easy Red 2 - Headless Dedicated Server";
        public const string PluginVersion = "0.1.0";

        internal static Plugin Instance;
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;
            _harmony = new Harmony(PluginGuid);

            Log.LogInfo($"{PluginName} v{PluginVersion} initialized.");
        }
    }
}
