using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace EasyRed2Headless
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "com.antigravity.easyred2.headless";
        public const string PluginName = "Easy Red 2 - Headless Dedicated Multiplayer Server";
        public const string PluginVersion = "0.1.0";

        internal static Plugin Instance;
        internal static new ManualLogSource Log;
        private Harmony _harmony;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;

            HeadlessConfig.Init(Config);

            _harmony = new Harmony(PluginGuid);
            HeadlessPatches.Apply(_harmony);

            ClassInjector.RegisterTypeInIl2Cpp<HeadlessServerController>();

            var hostGo = new GameObject("EasyRed2HeadlessServerHost");
            UnityEngine.Object.DontDestroyOnLoad(hostGo);
            hostGo.hideFlags = HideFlags.HideAndDontSave;
            hostGo.AddComponent<HeadlessServerController>();

            Log.LogInfo($"{PluginName} v{PluginVersion} initialized (ServerName='{HeadlessConfig.ServerName.Value}', Campaign='{HeadlessConfig.CampaignFilter.Value}').");
        }
    }
}
