using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace EasyRed2Headless
{
    public class HeadlessServerController : MonoBehaviour
    {
        public HeadlessServerController(IntPtr ptr) : base(ptr) { }

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        private enum StartupState
        {
            WaitingForMainMenu,
            OpeningMultiplayerTab,
            WaitingForPhotonMaster,
            SwitchingRegion,
            LaunchingCampaign,
            RunningInBattle
        }

        public static bool IsAutoStarting { get; private set; } = true;
        public static int RoomNameCollisionCount { get; set; } = 0;

        private StartupState _state = StartupState.WaitingForMainMenu;
        private float _stateTimer = 0f;
        private float _nextMaintenanceTime = 0f;
        private float _nextTelemetryTime = 0f;
        private bool _render3DCameras = false;
        private static bool _steamLockReleased = false;
        private static bool _loggedCampaignCatalog = false;
        private bool _syncedInitialAIState = false;

        // Cached telemetry for OnGUI (updated 1x/sec, zero per-frame allocation)
        private string _statusHeadline = "Initializing Headless Server...";
        private string _roomSummary = "Connecting...";
        private string _missionSummary = "Preparing Campaign...";
        private string _playersSummary = "Players: 0 / 0";
        private string _rulesSummary = "";
        private float _smoothedFps = 45f;

        private void Awake()
        {
            ApplyHeadlessPerformanceSettings(resizeWindow: true);
        }

        private void Update()
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            if (!_syncedInitialAIState)
            {
                _syncedInitialAIState = true;
                SyncCustomAIState(HeadlessConfig.EnableCustomAI.Value);
            }

            _smoothedFps += ((1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)) - _smoothedFps) * 0.05f;

            // Check if we are currently inside a loaded battle scene
            if (BattleManager.instance != null)
            {
                if (_state != StartupState.RunningInBattle)
                {
                    _state = StartupState.RunningInBattle;
                    IsAutoStarting = false;
                    Plugin.Log.LogInfo("[HeadlessServer] BattleManager active — entered RunningInBattle state.");
                }

                RunInBattleMaintenance();
                return;
            }

            // If we returned to MainMenu from a battle, reset state machine to relaunch
            if (_state == StartupState.RunningInBattle && MainMenu.instance != null)
            {
                Plugin.Log.LogWarning("[HeadlessServer] Returned to MainMenu unexpectedly; restarting server startup sequence.");
                _state = StartupState.WaitingForMainMenu;
                _stateTimer = 0f;
                IsAutoStarting = true;
            }

            RunStartupStateMachine();
        }

        private void RunStartupStateMachine()
        {
            _stateTimer += Time.unscaledDeltaTime;

            switch (_state)
            {
                case StartupState.WaitingForMainMenu:
                {
                    _statusHeadline = "Step 1/4: Waiting for MainMenu...";
                    if (MainMenu.instance != null && _stateTimer >= 3.0f)
                    {
                        ServerListMenu.maxServerNameLength = 64;
                        ApplyHeadlessPerformanceSettings(resizeWindow: false);
                        _state = StartupState.OpeningMultiplayerTab;
                        _stateTimer = 0f;
                    }
                    break;
                }

                case StartupState.OpeningMultiplayerTab:
                {
                    _statusHeadline = "Step 2/4: Opening Multiplayer & Authenticating...";
                    try
                    {
                        if (MainMenu.instance.multiplayer_tab != null)
                        {
                            Plugin.Log.LogInfo("[HeadlessServer] Opening MainMenu.multiplayer_tab to trigger Photon connection...");
                            MainMenu.instance.OpenTab(MainMenu.instance.multiplayer_tab, true);
                        }
                        _state = StartupState.WaitingForPhotonMaster;
                        _stateTimer = 0f;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[HeadlessServer] Error opening multiplayer tab: {ex}");
                        _stateTimer = 0f;
                    }
                    break;
                }

                case StartupState.WaitingForPhotonMaster:
                {
                    var auth = MultiplayerAuthenticator.instance;
                    string netState = PhotonNetwork.NetworkClientState.ToString();
                    _statusHeadline = $"Step 3/4: Connecting to Photon ({netState})...";

                    bool connected =
                        PhotonNetwork.IsConnectedAndReady &&
                        (PhotonNetwork.InLobby ||
                         PhotonNetwork.NetworkClientState == ClientState.ConnectedToMasterServer ||
                         PhotonNetwork.NetworkClientState == ClientState.JoinedLobby);

                    if (connected && auth != null && !MultiplayerAuthenticator.hopping && !MultiplayerAuthenticator.regionBusy)
                    {
                        LogAvailableRegions();

                        string targetRegion = ResolveTargetRegionId(HeadlessConfig.Region.Value);
                        string currentRegion = MultiplayerAuthenticator.CurrentRegionId;

                        if (!string.IsNullOrEmpty(targetRegion) &&
                            !string.Equals(currentRegion, targetRegion, StringComparison.OrdinalIgnoreCase))
                        {
                            Plugin.Log.LogInfo($"[HeadlessServer] Switching Photon region from '{currentRegion}' to '{targetRegion}'...");
                            MultiplayerAuthenticator.createRegionId = targetRegion;
                            auth.StartCoroutine(auth.ReconnectToNow(targetRegion, null));
                            _state = StartupState.SwitchingRegion;
                            _stateTimer = 0f;
                        }
                        else
                        {
                            _state = StartupState.LaunchingCampaign;
                            _stateTimer = 0f;
                        }
                    }
                    else if (_stateTimer > 25f)
                    {
                        Plugin.Log.LogWarning("[HeadlessServer] Photon connection timed out after 25s; retrying multiplayer tab...");
                        _state = StartupState.OpeningMultiplayerTab;
                        _stateTimer = 0f;
                    }
                    break;
                }

                case StartupState.SwitchingRegion:
                {
                    _statusHeadline = $"Step 3b/4: Switching Region to '{HeadlessConfig.Region.Value}'...";
                    var auth = MultiplayerAuthenticator.instance;
                    bool connected =
                        PhotonNetwork.IsConnectedAndReady &&
                        (PhotonNetwork.InLobby ||
                         PhotonNetwork.NetworkClientState == ClientState.ConnectedToMasterServer ||
                         PhotonNetwork.NetworkClientState == ClientState.JoinedLobby);

                    if (connected && auth != null && !MultiplayerAuthenticator.hopping && !MultiplayerAuthenticator.regionBusy && _stateTimer >= 1.5f)
                    {
                        Plugin.Log.LogInfo($"[HeadlessServer] Connected on region '{MultiplayerAuthenticator.CurrentRegionId}'.");
                        _state = StartupState.LaunchingCampaign;
                        _stateTimer = 0f;
                    }
                    else if (_stateTimer > 15f)
                    {
                        Plugin.Log.LogWarning("[HeadlessServer] Region switch timed out; proceeding with current region.");
                        _state = StartupState.LaunchingCampaign;
                        _stateTimer = 0f;
                    }
                    break;
                }

                case StartupState.LaunchingCampaign:
                {
                    _statusHeadline = $"Step 4/4: Launching '{HeadlessConfig.CampaignFilter.Value}' Campaign...";
                    if (_stateTimer < 1.0f) break;

                    try
                    {
                        LaunchConfiguredCampaignServer();
                        // Advance timer negatively so we don't re-invoke while scene loads
                        _stateTimer = -60f;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[HeadlessServer] Failed launching campaign server: {ex}");
                        _stateTimer = -10f;
                    }
                    break;
                }
            }
        }

        private void LaunchConfiguredCampaignServer()
        {
            ServerListMenu.maxServerNameLength = 64;

            // Stop any background ServerListMenu coroutines
            if (ServerListMenu.instance != null)
            {
                ServerListMenu.instance.ResetServerList();
            }

            var md = MatchData.data;
            if (md == null)
            {
                Plugin.Log.LogError("[HeadlessServer] MatchData.data is null!");
                return;
            }

            md.ResetSelectedBattles();
            ApplyConfiguredMatchSettings(md);
            EnsureCampaignBattlesPopulated(md);

            if (md.battles == null || md.battles.Count == 0)
            {
                Plugin.Log.LogError("[HeadlessServer] No battles could be populated into MatchData.data.battles!");
                return;
            }

            md.currentBattleId = 0;
            md.Initialize(true);
            md.SetMultiplayerRoomSettings();

            Plugin.Log.LogInfo(
                $"[HeadlessServer] Starting Campaign Server '{md.room_name}' on Region '{md.mp_region}' " +
                $"with {md.battles.Count} missions (First Mission: {md.battles[0]?.battle_reference?.GetUniqueId()}, RoomOptionsNull={MatchData.roomOptions == null})...");

            md.StartBattleLoading();
        }

        public static void ApplyConfiguredMatchSettings(MatchData md)
        {
            if (md == null) return;

            ServerListMenu.maxServerNameLength = 64;

            md.isMultiplayer = true;
            MatchData.actorType = MatchData.RoomActorType.master;
            string baseName = HeadlessConfig.ServerName.Value ?? "Headless";
            md.room_name = RoomNameCollisionCount > 0
                ? baseName + new string(' ', Mathf.Min(RoomNameCollisionCount, 8))
                : baseName;

            // MultiSelectOrdered keeps all campaign missions in chronological order while enabling Map Voting across the full campaign
            md.playType = MatchData.MissionsPlayType.MultiSelectOrdered;

            md.mp_voting_enabled = HeadlessConfig.EnableVoting.Value;
            md.mp_allow_unverified_clients = HeadlessConfig.AllowUnverifiedClients.Value;
            md.mp_allow_teamkillers = HeadlessConfig.AllowFriendlyFire.Value;
            md.mp_kick_teamkillers = HeadlessConfig.KickTeamkillers.Value;
            md.mp_allow_crosshair = HeadlessConfig.AllowCrosshair.Value;
            md.mp_allow_hitmarker = HeadlessConfig.AllowHitmarker.Value;
            md.mp_allow_tps = HeadlessConfig.AllowThirdPerson.Value;
            md.mp_has_admin = true;
            md.mp_visible = true;

            md.mp_max_players_invaders = Mathf.Clamp(HeadlessConfig.MaxPlayersInvaders.Value, 0, 16);
            md.mp_max_players_defenders = Mathf.Clamp(HeadlessConfig.MaxPlayersDefenders.Value, 0, 16);

            if (!string.IsNullOrEmpty(MultiplayerAuthenticator.CurrentRegionId))
            {
                md.mp_region = MultiplayerAuthenticator.CurrentRegionId;
            }
            else
            {
                md.mp_region = HeadlessConfig.Region.Value;
            }

            if (MatchData.Difficulty != null)
            {
                MatchData.Difficulty.SetDifficultyPresed(DifficultyValue.Normal);
            }
        }

        public static void EnsureCampaignBattlesPopulated(MatchData md)
        {
            if (md == null) return;
            if (md.battles != null && md.battles.Count > 1) return;

            var campaigns = VanillaCampaignsList.vanilla_campaign_data;
            if (campaigns == null || campaigns.Length == 0)
            {
                Plugin.Log.LogWarning("[HeadlessServer] VanillaCampaignsList.vanilla_campaign_data is empty!");
                return;
            }

            if (!_loggedCampaignCatalog)
            {
                _loggedCampaignCatalog = true;
                Plugin.Log.LogInfo($"[HeadlessServer] Discovered {campaigns.Length} vanilla campaigns:");
                for (int i = 0; i < campaigns.Length; i++)
                {
                    var c = campaigns[i];
                    if (c == null) continue;
                    Plugin.Log.LogInfo($"  [{i}] id='{c.campaing_id}', name='{c.GetName()}', missions={c.battles?.Count ?? 0}");
                }
            }

            string filter = HeadlessConfig.CampaignFilter.Value ?? "Anzio";
            CampaignTreeData selectedCampaign = null;

            for (int i = 0; i < campaigns.Length; i++)
            {
                var c = campaigns[i];
                if (c == null || c.battles == null || c.battles.Count == 0) continue;

                string id = c.campaing_id ?? "";
                string name = c.GetName() ?? "";

                if (id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    selectedCampaign = c;
                    break;
                }
            }

            if (selectedCampaign == null)
            {
                selectedCampaign = campaigns[0];
                Plugin.Log.LogWarning($"[HeadlessServer] Campaign matching '{filter}' not found; falling back to '{selectedCampaign?.GetName()}'.");
            }
            else
            {
                Plugin.Log.LogInfo($"[HeadlessServer] Selected campaign: id='{selectedCampaign.campaing_id}', name='{selectedCampaign.GetName()}' ({selectedCampaign.battles.Count} missions).");
            }

            if (md.battles != null)
            {
                md.battles.Clear();
            }

            int added = 0;
            for (int i = 0; i < selectedCampaign.battles.Count; i++)
            {
                var battleRef = selectedCampaign.battles[i];
                if (battleRef == null) continue;

                try
                {
                    if (!battleRef.AllowMultiplayer()) continue;

                    IBattle ibattle = battleRef.LoadBattle();
                    if (ibattle == null) continue;

                    BattleData bd = ibattle.GetBattleData();
                    if (bd == null) continue;

                    MatchBattle mb = new MatchBattle();
                    mb.AssignBattleDataFromMission(ibattle);
                    // Preserve the original CampaignTreeVanillaStreamingBattleData / CampaignTreeVanillaBattleData reference!
                    // AssignBattleDataFromMission overwrites battle_reference with a local mission_editor path if not restored.
                    mb.battle_reference = battleRef;

                    if (string.IsNullOrEmpty(mb.selected_faction))
                    {
                        mb.selected_faction = bd.GetFirstValidFaction();
                    }

                    short maxAllies = 0;
                    short maxAxis = 0;
                    bd.GetMaxUnitsForThisSystemProportional(true, out maxAllies, out maxAxis);

                    if (maxAllies > 0)
                    {
                        mb.maxAllies = maxAllies;
                        mb.proportionalAlliesRefValue = maxAllies;
                    }
                    if (maxAxis > 0)
                    {
                        mb.maxAxis = maxAxis;
                        mb.proportionalAxisRefValue = maxAxis;
                    }

                    mb.ticketsMultiplier = Mathf.Clamp(HeadlessConfig.TicketsMultiplier.Value, 0.25f, 5.0f);

                    if (!md.AddBattle(mb))
                    {
                        md.battles.Add(mb);
                    }
                    added++;
                    Plugin.Log.LogInfo($"  + Mission [{added}]: '{ibattle.GetListedName()}' (id={mb.battle_reference.GetUniqueId()}, Allies={mb.maxAllies}, Axis={mb.maxAxis})");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HeadlessServer] Skipped mission index {i} due to error: {ex.Message}");
                }
            }

            md.vanillaMissionsCount = added;
            Plugin.Log.LogInfo($"[HeadlessServer] Populated {added} missions into MatchData.data.battles (vanillaMissionsCount={md.vanillaMissionsCount}).");
        }

        private void RunInBattleMaintenance()
        {
            float now = Time.unscaledTime;

            // Ensure Photon message queue and FixedUpdate timeScale stay active during room creation
            if (!PhotonNetwork.InRoom)
            {
                if (!PhotonNetwork.IsMessageQueueRunning)
                {
                    PhotonNetwork.IsMessageQueueRunning = true;
                }
                if (Time.timeScale < 1f)
                {
                    Time.timeScale = 1f;
                }
                try
                {
                    if (PhotonHandler.Instance != null)
                    {
                        PhotonHandler.Instance.Dispatch();
                    }
                    PhotonNetwork.NetworkingClient?.LoadBalancingPeer?.SendOutgoingCommands();
                }
                catch { }
            }

            if (now >= _nextMaintenanceTime)
            {
                _nextMaintenanceTime = now + 2.0f;

                ApplyHeadlessPerformanceSettings(resizeWindow: false);

                // Keep RespawnPanel alive in unspawned spectator state so the host never occupies a faction slot or spawns a dummy body
                if (RespawnPanel.instance != null)
                {
                    try
                    {
                        RespawnPanel.KeepRespawningViewAlive();
                    }
                    catch { }
                }

                // Disable 3D cameras in battle unless preview is toggled on
                if (HeadlessConfig.Disable3DCamerasInBattle.Value && !_render3DCameras)
                {
                    var cameras = Camera.allCameras;
                    if (cameras != null)
                    {
                        for (int i = 0; i < cameras.Length; i++)
                        {
                            var cam = cameras[i];
                            if (cam != null && cam.enabled)
                            {
                                cam.enabled = false;
                            }
                        }
                    }
                }
            }

            if (now >= _nextTelemetryTime)
            {
                _nextTelemetryTime = now + 1.0f;
                RefreshBattleTelemetry();
            }
        }

        private void RefreshBattleTelemetry()
        {
            try
            {
                var room = PhotonNetwork.CurrentRoom;
                var md = MatchData.data;

                string roomName = md != null ? md.room_name : HeadlessConfig.ServerName.Value;
                string region = PhotonNetwork.CloudRegion ?? (md != null ? md.mp_region : "us");
                int playerCount = room != null ? room.PlayerCount : 1;
                // Subtract 1 for the unspawned headless host when reporting human players
                int humanPlayers = Mathf.Max(0, playerCount - 1);
                int maxPlayers = md != null ? (md.mp_max_players_invaders + md.mp_max_players_defenders) : 16;

                int battleIdx = md != null ? (md.currentBattleId + 1) : 1;
                int battleTotal = md?.battles != null ? md.battles.Count : 1;
                string missionName = BattleManager.instance?.battleData?.GetLocationName() ?? "Loading...";

                int activeBots = 0;
                if (Creature.allCreatures != null)
                {
                    activeBots = Creature.allCreatures.Count;
                }

                _statusHeadline = $"LIVE HEADLESS SERVER ({_smoothedFps:F0} FPS)";
                _roomSummary = $"Server: {roomName}  |  Region: {region}  |  Room ID: {room?.Name ?? "N/A"}";
                _missionSummary = $"Campaign: {HeadlessConfig.CampaignFilter.Value} ({battleIdx}/{battleTotal}) — {missionName}";
                _playersSummary = $"Human Players: {humanPlayers} / {maxPlayers} ({md?.mp_max_players_invaders ?? 8}v{md?.mp_max_players_defenders ?? 8} PvP)  |  Active Units: {activeBots}";
                _rulesSummary = $"Diff: Normal  |  FF: {(md?.mp_allow_teamkillers == true ? "ON" : "OFF")}  |  Kick TK: {(md?.mp_kick_teamkillers == true ? "ON" : "OFF")}  |  Voting: {(md?.mp_voting_enabled == true ? "ON" : "OFF")}  |  Custom AI: {(HeadlessConfig.EnableCustomAI.Value ? "ON" : "OFF")}";

                if (Mathf.FloorToInt(Time.unscaledTime) % 5 == 0)
                {
                    Plugin.Log.LogInfo($"[Telemetry] InRoom={PhotonNetwork.InRoom} | NetState={PhotonNetwork.NetworkClientState} | Server={PhotonNetwork.Server} | TimeScale={Time.timeScale:F1} | MsgQ={PhotonNetwork.IsMessageQueueRunning} | {_roomSummary} | {_missionSummary} | {_playersSummary} | FPS={_smoothedFps:F0}");
                    LogAiDiagnostics();
                }
            }
            catch { }
        }

        private void LogAiDiagnostics()
        {
            try
            {
                var bm = BattleManager.instance;
                var rmCam = ResourcesManager.mainCamera;
                var mainCam = Camera.main;

                string playersInfo = "";
                var plist = PhotonNetwork.PlayerList;
                if (plist != null)
                {
                    for (int i = 0; i < plist.Length; i++)
                    {
                        var p = plist[i];
                        if (p != null)
                        {
                            playersInfo += $"P{i}[#{p.ActorNumber},'{p.NickName}',master={p.IsMasterClient},local={p.IsLocal}] ";
                        }
                    }
                }

                Plugin.Log.LogInfo(
                    $"[AIDiag] IsMaster={PhotonNetwork.IsMasterClient} | Players({PhotonNetwork.CurrentRoom?.PlayerCount})={playersInfo}| " +
                    $"BM.IsPlaying={bm?.IsPlaying()} | BM.IsBattleActive={BattleManager.IsBattleActive()} | BM.phase={bm?.currentPhaseNum} | " +
                    $"RM.mainCam={(rmCam != null ? $"{rmCam.name}(en={rmCam.enabled},pos={rmCam.transform.position})" : "NULL")} | " +
                    $"Creature.seqIdx={Creature.sequentialUpdateIndex} | Time.time={Time.time:F1}");

                if (Creature.allCreatures == null) return;

                int total = Creature.allCreatures.Count;
                int aliveSoldiers = 0;
                int onVehicleCount = 0;
                int movingCount = 0;
                int hasPathCount = 0;
                int hasDestCount = 0;
                int withTarget = 0;
                int pressingTrigger = 0;
                int alliesCount = 0;
                int axisCount = 0;

                var allies = new List<Soldier>(40);
                var axis = new List<Soldier>(40);
                string sampleInfo = "";

                for (int i = 0; i < total; i++)
                {
                    var c = Creature.allCreatures[i];
                    if (c == null || !c.IsAlive) continue;
                    var s = c.TryCast<Soldier>();
                    if (s == null) continue;
                    aliveSoldiers++;

                    if (s.IsOnVehicle()) onVehicleCount++;
                    if (s.HasDestinationAssigned) hasDestCount++;
                    if (s.IsPressingTrigger) pressingTrigger++;

                    string fac = s.faction ?? "";
                    if (fac.EndsWith("_allies", StringComparison.OrdinalIgnoreCase))
                        allies.Add(s);
                    else if (fac.EndsWith("_axis", StringComparison.OrdinalIgnoreCase))
                        axis.Add(s);

                    var ai = s.aiController;
                    if (ai != null)
                    {
                        if (ai.IsMoving()) movingCount++;
                        if (ai.HasPath) hasPathCount++;
                        if (ai.visibleTarget != null || s.CurrentVisibleTarget != null) withTarget++;
                    }

                    if (aliveSoldiers <= 4 && ai != null)
                    {
                        var veh = s.GetCurrentVehicle();
                        var aiv = veh != null ? veh.GetComponent<AIVehicle>() : null;
                        string vehStr = veh != null
                            ? $"{veh.name}(drv={veh.GetDriver()?.name_surname},phys={veh.IsPhysicsEnabled},lod={veh.IsLodded()},aiDest={aiv?.destinationActive},rch={aiv?.DestinationReached},reqDrv={aiv?.RequiresDriver})"
                            : "no";
                        sampleInfo += $" | S{aliveSoldiers}[{s.name_surname},fac={s.faction},onVeh={vehStr},rnd={s.IsRendered},cc={s.m_controller?.enabled},p_mov={s.Params?.canMove},p_chk={s.Params?.checkForEnemies},sqOrd={s.joinedSquad?.order},hasDest={s.HasDestinationAssigned},hasPath={ai.HasPath},isMov={ai.IsMoving()},mvTime={ai.IsMoveTime},spd={ai.speed:F1},vis={(ai.visibleTarget != null)},pos={s.transform.position},mvDest={ai.MoveDestination}]";
                    }
                }

                alliesCount = allies.Count;
                axisCount = axis.Count;

                // Find closest opposing pair on foot and overall
                float minOverallDist = 999999f;
                Soldier bestA = null, bestB = null;
                float minFootDist = 999999f;
                Soldier footA = null, footB = null;

                for (int i = 0; i < allies.Count; i++)
                {
                    var a = allies[i];
                    Vector3 pa = a.transform.position;
                    bool aFoot = !a.IsOnVehicle();
                    for (int j = 0; j < axis.Count; j++)
                    {
                        var b = axis[j];
                        float d = Vector3.Distance(pa, b.transform.position);
                        if (d < minOverallDist)
                        {
                            minOverallDist = d;
                            bestA = a;
                            bestB = b;
                        }
                        if (aFoot && !b.IsOnVehicle() && d < minFootDist)
                        {
                            minFootDist = d;
                            footA = a;
                            footB = b;
                        }
                    }
                }

                string pairInfo = "";
                Soldier pA = footA ?? bestA;
                Soldier pB = footB ?? bestB;
                float pDist = footA != null ? minFootDist : minOverallDist;
                if (pA != null && pB != null)
                {
                    bool aSeesB = false, bSeesA = false;
                    float camoA = -1f, camoB = -1f;
                    try { aSeesB = pA.CanSee(pB.Cast<Spottable>()); } catch { }
                    try { bSeesA = pB.CanSee(pA.Cast<Spottable>()); } catch { }
                    try { camoA = pA.CamoDistanceAwareEnemies(false); } catch { }
                    try { camoB = pB.CamoDistanceAwareEnemies(false); } catch { }
                    string rayHit = "none";
                    try
                    {
                        Vector3 eyeA = pA.FacePosition();
                        Vector3 spotB = pB.SpotPosition();
                        Vector3 dirAB = (spotB - eyeA).normalized;
                        if (Physics.Raycast(eyeA, dirAB, out RaycastHit hit, 1000f, ResourcesManager.visionMask))
                        {
                            var conn = Creature.GetConnectedSpottable(hit.transform);
                            rayHit = $"{hit.collider?.name}(lyr={hit.collider?.gameObject.layer},d={hit.distance:F1},spot={(conn != null ? conn.TryCast<Creature>()?.name ?? "yes" : "no")})";
                        }
                    }
                    catch { }
                    pairInfo = $" | ClosestPair(foot={(footA != null)},dist={pDist:F1}m,overall={minOverallDist:F1}m,rayAB={rayHit}): A[{pA.name_surname},onVeh={pA.IsOnVehicle()},rnd={pA.IsRendered},cc={pA.m_controller?.enabled},pos={pA.transform.position},camo={camoA:F0},seesB={aSeesB},vis={(pA.aiController?.visibleTarget != null)},canFire={pA.aiController?.HasEnemyAndCanFireAtHim}] vs B[{pB.name_surname},onVeh={pB.IsOnVehicle()},rnd={pB.IsRendered},cc={pB.m_controller?.enabled},pos={pB.transform.position},camo={camoB:F0},seesA={bSeesA},vis={(pB.aiController?.visibleTarget != null)},canFire={pB.aiController?.HasEnemyAndCanFireAtHim}]";
                }

                Plugin.Log.LogInfo($"[AIDiag-Units] alive={aliveSoldiers}/{total} (Allies={alliesCount},Axis={axisCount}) | onVeh={onVehicleCount} | hasDest={hasDestCount} | hasPath={hasPathCount} | moving={movingCount} | withTarget={withTarget} | firing={pressingTrigger}{pairInfo}{sampleInfo}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AIDiag] Error: {ex}");
            }
        }

        private void ApplyHeadlessPerformanceSettings(bool resizeWindow)
        {
            try
            {
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = Mathf.Clamp(HeadlessConfig.TargetTickRate.Value, 20, 120);
                // Keep AudioListener unpaused at 0 volume so FMOD virtual channels play and release normally instead of piling up
                AudioListener.pause = false;
                AudioListener.volume = 0f;

                if (resizeWindow)
                {
                    Screen.SetResolution(540, 300, false);
                }

                IntPtr hwnd = GetActiveWindow();
                if (hwnd != IntPtr.Zero)
                {
                    SetWindowTextW(hwnd, $"Easy Red 2 — Headless Server ({HeadlessConfig.ServerName.Value})");
                }
            }
            catch { }
        }

        public static void TryReleaseSteamLock()
        {
            if (_steamLockReleased) return;
            _steamLockReleased = true;

            try
            {
                if (SteamManager._instance != null)
                {
                    Plugin.Log.LogInfo("[HeadlessServer] Calling SteamManager._instance.ShutdownSteam() so local Steam client frees AppID 1392960...");
                    SteamManager._instance.ShutdownSteam();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessServer] TryReleaseSteamLock warning: {ex.Message}");
            }
        }

        private static void LogAvailableRegions()
        {
            try
            {
                var mpRegions = MultiplayerAuthenticator.mpRegions;
                if (mpRegions == null) return;
                var regions = new List<string>();
                for (int i = 0; i < mpRegions.Length; i++)
                {
                    var r = mpRegions[i];
                    if (r != null)
                    {
                        regions.Add($"{r.region_id} ({r.region_name})");
                    }
                }
                Plugin.Log.LogInfo($"[HeadlessServer] Photon Regions: [{string.Join(", ", regions)}], Current='{MultiplayerAuthenticator.CurrentRegionId}', Best='{MultiplayerAuthenticator.BestRegionId}'");
            }
            catch { }
        }

        private static string ResolveTargetRegionId(string configuredRegion)
        {
            if (string.IsNullOrWhiteSpace(configuredRegion) ||
                string.Equals(configuredRegion, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return MultiplayerAuthenticator.CurrentRegionId;
            }

            var mpRegions = MultiplayerAuthenticator.mpRegions;
            if (mpRegions != null)
            {
                for (int i = 0; i < mpRegions.Length; i++)
                {
                    var r = mpRegions[i];
                    if (r != null && string.Equals(r.region_id, configuredRegion, StringComparison.OrdinalIgnoreCase))
                    {
                        return r.region_id;
                    }
                }
            }

            return configuredRegion;
        }

        public static void SyncCustomAIState(bool enable)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var modCfgType = asm.GetType("EasyRed2HumanAI.VisionConfig", false);
                    if (modCfgType != null)
                    {
                        var field = modCfgType.GetField("EnableMod", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        var entry = field?.GetValue(null) as BepInEx.Configuration.ConfigEntry<bool>;
                        if (entry != null)
                        {
                            entry.Value = enable;
                            Plugin.Log.LogInfo($"[HeadlessServer] Custom AI (EasyRed2HumanAI) toggled to: {(enable ? "ENABLED" : "DISABLED (Vanilla AI)")}");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessServer] SyncCustomAIState note: {ex.Message}");
            }
        }

        private void OnGUI()
        {
            if (!HeadlessConfig.EnableHeadlessServer.Value) return;

            GUI.color = new Color(0.08f, 0.09f, 0.11f, 0.94f);
            GUI.Box(new Rect(8, 8, Screen.width - 16, Screen.height - 16), "");
            GUI.color = Color.white;

            float y = 16f;
            GUI.Label(new Rect(20, y, Screen.width - 40, 24), $"<b>[EASY RED 2 DEDICATED SERVER]</b> — {_statusHeadline}");
            y += 26f;

            GUI.Label(new Rect(20, y, Screen.width - 40, 22), _roomSummary);
            y += 22f;

            GUI.Label(new Rect(20, y, Screen.width - 40, 22), _missionSummary);
            y += 22f;

            GUI.Label(new Rect(20, y, Screen.width - 40, 22), _playersSummary);
            y += 22f;

            GUI.Label(new Rect(20, y, Screen.width - 40, 22), _rulesSummary);
            y += 30f;

            if (_state == StartupState.RunningInBattle)
            {
                string camLabel = _render3DCameras ? "3D Camera: ON" : "3D Camera: OFF";
                if (GUI.Button(new Rect(20, y, 145, 28), camLabel))
                {
                    _render3DCameras = !_render3DCameras;
                    var cameras = Camera.allCameras;
                    if (cameras != null)
                    {
                        for (int i = 0; i < cameras.Length; i++)
                        {
                            if (cameras[i] != null) cameras[i].enabled = _render3DCameras;
                        }
                    }
                }

                string aiLabel = HeadlessConfig.EnableCustomAI.Value ? "Custom AI: ON" : "Custom AI: OFF";
                if (GUI.Button(new Rect(175, y, 145, 28), aiLabel))
                {
                    HeadlessConfig.EnableCustomAI.Value = !HeadlessConfig.EnableCustomAI.Value;
                    SyncCustomAIState(HeadlessConfig.EnableCustomAI.Value);
                    RefreshBattleTelemetry();
                }

                if (GUI.Button(new Rect(330, y, 165, 28), "Skip Next Mission"))
                {
                    try
                    {
                        if (BattleLoader.instance != null)
                        {
                            Plugin.Log.LogInfo("[HeadlessServer] Operator triggered Skip To Next Mission.");
                            BattleLoader.instance.LoadNextMpMatch();
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[HeadlessServer] Failed skipping mission: {ex}");
                    }
                }
            }
        }
    }
}
