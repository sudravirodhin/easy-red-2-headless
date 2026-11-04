using BepInEx.Configuration;

namespace EasyRed2Headless
{
    public static class HeadlessConfig
    {
        public static ConfigEntry<bool> EnableHeadlessServer;
        public static ConfigEntry<string> ServerName;
        public static ConfigEntry<string> Region;
        public static ConfigEntry<string> CampaignFilter;
        public static ConfigEntry<bool> EnableVoting;
        public static ConfigEntry<bool> AllowUnverifiedClients;
        public static ConfigEntry<bool> AllowFriendlyFire;
        public static ConfigEntry<bool> KickTeamkillers;
        public static ConfigEntry<bool> AllowCrosshair;
        public static ConfigEntry<bool> AllowHitmarker;
        public static ConfigEntry<bool> AllowThirdPerson;
        public static ConfigEntry<int> MaxPlayersInvaders;
        public static ConfigEntry<int> MaxPlayersDefenders;
        public static ConfigEntry<float> TicketsMultiplier;
        public static ConfigEntry<int> TargetTickRate;
        public static ConfigEntry<bool> Disable3DCamerasInBattle;
        public static ConfigEntry<bool> ShutdownSteamAfterRoomCreated;

        public static void Init(ConfigFile config)
        {
            EnableHeadlessServer = config.Bind(
                "1. General",
                "EnableHeadlessServer",
                true,
                "Master toggle. When true, automatically boots into Headless Dedicated Server mode.");

            ServerName = config.Bind(
                "1. General",
                "ServerName",
                "US South 1Gbps",
                "Public server name displayed in the Easy Red 2 Multiplayer Server Browser (supports >8 characters).");

            Region = config.Bind(
                "1. General",
                "Region",
                "us",
                "Photon region ID to host the server in (e.g., 'us', 'eu', 'asia', 'au', 'sa', 'jp').");

            CampaignFilter = config.Bind(
                "2. Rotation",
                "CampaignFilter",
                "Anzio",
                "Name or ID substring of the campaign to load into the server rotation (e.g., 'Anzio', 'Normandy', 'Stalingrad', 'Tunisia', 'Kos', 'Kwajalein', 'Makin', 'Ardennes', 'Shanghai', 'Nanking').");

            EnableVoting = config.Bind(
                "2. Rotation",
                "EnableVoting",
                true,
                "Enable end-of-match map voting across the loaded campaign missions.");

            TicketsMultiplier = config.Bind(
                "2. Rotation",
                "TicketsMultiplier",
                1.0f,
                "Multiplier for attacker reinforcement tickets (1.0 = default vanilla tickets).");

            MaxPlayersInvaders = config.Bind(
                "3. Rules & Players",
                "MaxPlayersInvaders",
                8,
                "Maximum human player slots for the Invaders faction (PvP enabled when both Invaders and Defenders > 0).");

            MaxPlayersDefenders = config.Bind(
                "3. Rules & Players",
                "MaxPlayersDefenders",
                8,
                "Maximum human player slots for the Defenders faction (PvP enabled when both Invaders and Defenders > 0).");

            AllowFriendlyFire = config.Bind(
                "3. Rules & Players",
                "AllowFriendlyFire",
                true,
                "Enable friendly fire / team damage (mp_allow_teamkillers).");

            KickTeamkillers = config.Bind(
                "3. Rules & Players",
                "KickTeamkillers",
                true,
                "Enable automatic teamkiller kick rules (mp_kick_teamkillers).");

            AllowUnverifiedClients = config.Bind(
                "3. Rules & Players",
                "AllowUnverifiedClients",
                true,
                "Allow unverified clients to join (also facilitates local dual-instance connections).");

            AllowCrosshair = config.Bind(
                "3. Rules & Players",
                "AllowCrosshair",
                true,
                "Allow weapon crosshairs for connected players.");

            AllowHitmarker = config.Bind(
                "3. Rules & Players",
                "AllowHitmarker",
                true,
                "Allow hitmarkers for connected players.");

            AllowThirdPerson = config.Bind(
                "3. Rules & Players",
                "AllowThirdPerson",
                true,
                "Allow third-person camera view for connected players.");

            TargetTickRate = config.Bind(
                "4. Performance & Headless",
                "TargetTickRate",
                45,
                "Target simulation frame rate for the headless server (45 or 60 recommended).");

            Disable3DCamerasInBattle = config.Bind(
                "4. Performance & Headless",
                "Disable3DCamerasInBattle",
                true,
                "Disable all 3D Camera rendering while in a match to drop GPU usage to ~0% while keeping OnGUI telemetry active.");

            ShutdownSteamAfterRoomCreated = config.Bind(
                "4. Performance & Headless",
                "ShutdownSteamAfterRoomCreated",
                true,
                "Shut down the headless process's SteamAPI pipe after the Photon room is created so you can launch your normal Easy Red 2 client from Steam on the same PC.");
        }
    }
}
