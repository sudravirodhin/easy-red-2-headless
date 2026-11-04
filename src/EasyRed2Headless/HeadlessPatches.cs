using System;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace EasyRed2Headless
{
    public static class HeadlessPatches
    {
        public static void Apply(Harmony harmony)
        {
            try
            {
                // 1. Lift the 8-character server name cap in ServerListMenu
                var slmAwake = AccessTools.Method(typeof(ServerListMenu), nameof(ServerListMenu.Awake));
                if (slmAwake != null)
                {
                    harmony.Patch(slmAwake, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(ServerListMenu_Awake_Postfix)));
                }

                // 2. Skip multi-region server browser scanning while headless auto-start is connecting
                var slmRefresh = AccessTools.Method(typeof(ServerListMenu), nameof(ServerListMenu.OnRefreshPressed));
                if (slmRefresh != null)
                {
                    harmony.Patch(slmRefresh, prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(ServerListMenu_OnRefreshPressed_Prefix)));
                }

                // 3. Enforce headless server settings & campaign rotation in MatchData.Initialize and SetMultiplayerRoomSettings
                var mdInit = AccessTools.Method(typeof(MatchData), nameof(MatchData.Initialize));
                if (mdInit != null)
                {
                    harmony.Patch(mdInit, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(MatchData_Initialize_Postfix)));
                }

                var mdSetRoom = AccessTools.Method(typeof(MatchData), nameof(MatchData.SetMultiplayerRoomSettings));
                if (mdSetRoom != null)
                {
                    harmony.Patch(
                        mdSetRoom,
                        prefix: new HarmonyMethod(typeof(HeadlessPatches), nameof(MatchData_SetMultiplayerRoomSettings_Prefix)),
                        postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(MatchData_SetMultiplayerRoomSettings_Postfix)));
                }

                // 4. Loop the campaign infinitely when the final mission finishes
                var mdNextBattle = AccessTools.Method(typeof(MatchData), nameof(MatchData.NextBattle));
                if (mdNextBattle != null)
                {
                    harmony.Patch(mdNextBattle, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(MatchData_NextBattle_Postfix)));
                }

                // 5. Log room creation & free Steam app lock once room is live
                var blJoined = AccessTools.Method(typeof(BattleLoader), nameof(BattleLoader.OnJoinedRoom));
                if (blJoined != null)
                {
                    harmony.Patch(blJoined, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(BattleLoader_OnJoinedRoom_Postfix)));
                }

                var blCreateFailed = AccessTools.Method(typeof(BattleLoader), nameof(BattleLoader.OnCreateRoomFailed));
                if (blCreateFailed != null)
                {
                    harmony.Patch(blCreateFailed, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(BattleLoader_OnCreateRoomFailed_Postfix)));
                }

                var blJoinFailed = AccessTools.Method(typeof(BattleLoader), nameof(BattleLoader.OnJoinRoomFailed));
                if (blJoinFailed != null)
                {
                    harmony.Patch(blJoinFailed, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(BattleLoader_OnJoinRoomFailed_Postfix)));
                }

                var blDisconnected = AccessTools.Method(typeof(BattleLoader), nameof(BattleLoader.OnDisconnected));
                if (blDisconnected != null)
                {
                    harmony.Patch(blDisconnected, postfix: new HarmonyMethod(typeof(HeadlessPatches), nameof(BattleLoader_OnDisconnected_Postfix)));
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Failed applying patches: {ex}");
            }
        }

        public static void ServerListMenu_Awake_Postfix()
        {
            ServerListMenu.maxServerNameLength = 64;
        }

        public static bool ServerListMenu_OnRefreshPressed_Prefix()
        {
            if (HeadlessConfig.EnableHeadlessServer.Value && HeadlessServerController.IsAutoStarting)
            {
                Plugin.Log.LogInfo("[HeadlessPatches] Skipping ServerListMenu multi-region refresh scan during headless startup.");
                return false;
            }
            return true;
        }

        public static void MatchData_Initialize_Postfix(MatchData __instance, bool isRoomCreator)
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value || !isRoomCreator) return;

            try
            {
                ServerListMenu.maxServerNameLength = 64;
                HeadlessServerController.ApplyConfiguredMatchSettings(__instance);
                HeadlessServerController.EnsureCampaignBattlesPopulated(__instance);
                Plugin.Log.LogInfo($"[HeadlessPatches] MatchData.Initialize complete: room_name='{__instance.room_name}', battles={__instance.battles?.Count ?? 0}, playType={__instance.playType}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in MatchData_Initialize_Postfix: {ex}");
            }
        }

        public static void MatchData_SetMultiplayerRoomSettings_Prefix(MatchData __instance)
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            try
            {
                ServerListMenu.maxServerNameLength = 64;
                HeadlessServerController.ApplyConfiguredMatchSettings(__instance);
                HeadlessServerController.EnsureCampaignBattlesPopulated(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in MatchData_SetMultiplayerRoomSettings_Prefix: {ex}");
            }
        }

        public static void MatchData_SetMultiplayerRoomSettings_Postfix(MatchData __instance)
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            try
            {
                var ro = MatchData.roomOptions;
                int maxPlayers = ro != null ? ro.MaxPlayers : -1;
                int propCount = ro?.CustomRoomProperties != null ? ro.CustomRoomProperties.Count : -1;

                Plugin.Log.LogInfo(
                    $"[HeadlessPatches] SetMultiplayerRoomSettings -> RoomName='{__instance.room_name}', " +
                    $"Region='{__instance.mp_region}', Voting={__instance.mp_voting_enabled}, " +
                    $"FriendlyFire={__instance.mp_allow_teamkillers}, KickTK={__instance.mp_kick_teamkillers}, " +
                    $"UnverifiedAllowed={__instance.mp_allow_unverified_clients}, " +
                    $"Slots={__instance.mp_max_players_invaders}v{__instance.mp_max_players_defenders} (RoomOptions.MaxPlayers={maxPlayers}, Props={propCount}, maxPlayersOnline={ServerListMenu.maxPlayersOnline}), " +
                    $"Battles={__instance.battles?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in MatchData_SetMultiplayerRoomSettings_Postfix: {ex}");
            }
        }

        public static void MatchData_NextBattle_Postfix(MatchData __instance, ref bool __result)
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            if (!__result && __instance.battles != null && __instance.battles.Count > 0)
            {
                Plugin.Log.LogInfo($"[HeadlessPatches] Campaign rotation reached final mission; looping back to mission 1 of {__instance.battles.Count}.");
                __instance.currentBattleId = 0;
                __result = true;
            }
        }

        public static void BattleLoader_OnJoinedRoom_Postfix()
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            try
            {
                var room = PhotonNetwork.CurrentRoom;
                var md = MatchData.data;
                Plugin.Log.LogInfo(
                    $"=== [HEADLESS SERVER LIVE] ===\n" +
                    $"  Public Server Name : {md?.room_name}\n" +
                    $"  Photon Room ID     : {room?.Name}\n" +
                    $"  Region             : {PhotonNetwork.CloudRegion} (Authenticator: {MultiplayerAuthenticator.CurrentRegionId})\n" +
                    $"  Max Players        : {room?.MaxPlayers} ({md?.mp_max_players_invaders} Invaders vs {md?.mp_max_players_defenders} Defenders)\n" +
                    $"  Campaign Missions  : {md?.battles?.Count ?? 0} (Current Index: {md?.currentBattleId})\n" +
                    $"  Map Voting         : {md?.mp_voting_enabled}\n" +
                    $"  Friendly Fire      : {md?.mp_allow_teamkillers} | Auto-Kick TK: {md?.mp_kick_teamkillers}\n" +
                    $"  Local Player UserId: {PhotonNetwork.LocalPlayer?.UserId}");

                if (HeadlessConfig.ShutdownSteamAfterRoomCreated.Value)
                {
                    HeadlessServerController.TryReleaseSteamLock();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in BattleLoader_OnJoinedRoom_Postfix: {ex}");
            }
        }

        public static void BattleLoader_OnCreateRoomFailed_Postfix(short returnCode, string message)
        {
            Plugin.Log.LogError($"[HeadlessPatches] Photon OnCreateRoomFailed! Code={returnCode}, Message='{message}'");
        }

        public static void BattleLoader_OnJoinRoomFailed_Postfix(short returnCode, string message)
        {
            Plugin.Log.LogError($"[HeadlessPatches] Photon OnJoinRoomFailed! Code={returnCode}, Message='{message}'");
        }

        public static void BattleLoader_OnDisconnected_Postfix(DisconnectCause cause)
        {
            Plugin.Log.LogWarning($"[HeadlessPatches] Photon OnDisconnected! Cause={cause}");
        }
    }
}
