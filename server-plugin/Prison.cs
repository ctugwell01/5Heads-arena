using Oxide.Core;
using Newtonsoft.Json;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace Oxide.Plugins
{
    [Info("Prison", "5HeadNN", "3.4.4")]
    [Description("Prison system with detailed in-game admin UI — /prison to open")]
    public class Prison : RustPlugin
    {
        [PluginReference] Plugin RaceTrack;
        [PluginReference] Plugin Payback3;

        private Dictionary<ulong, Vector3>         jailedPlayers    = new Dictionary<ulong, Vector3>();
        private Dictionary<ulong, Vector3>         playerCells      = new Dictionary<ulong, Vector3>();
        private Dictionary<ulong, string>          originalNames    = new Dictionary<ulong, string>();
        private Dictionary<ulong, List<SavedItem>> savedInventories = new Dictionary<ulong, List<SavedItem>>();
        private Dictionary<ulong, string>          jailReasons      = new Dictionary<ulong, string>();
        private Dictionary<ulong, long>            jailStartTimes   = new Dictionary<ulong, long>();
        private Dictionary<ulong, long>            jailDurations    = new Dictionary<ulong, long>(); // seconds; 0/missing = indefinite
        private Dictionary<ulong, Dictionary<string, int>> jailOffenseCounts = new Dictionary<ulong, Dictionary<string, int>>(); // per-player, per-reason strike count (for escalation ladders)
        private HashSet<ulong>                     teleporting      = new HashSet<ulong>();
        private Dictionary<ulong, string>          pendingJails     = new Dictionary<ulong, string>();
        private HashSet<ulong>                     fakeTimerActive  = new HashSet<ulong>();
        private HashSet<ulong>                     heliActive       = new HashSet<ulong>();
        private HashSet<ulong>                     deathActive      = new HashSet<ulong>();
        private List<Vector3>                      cellLocations    = new List<Vector3>();
        private HashSet<ulong>                     yardActive       = new HashSet<ulong>();
        private HashSet<ulong>                     escapeAlarmActive= new HashSet<ulong>();
        private readonly List<ulong>               jailedUIDs       = new List<ulong>();
        private HashSet<ulong>                     _pendingRejail   = new HashSet<ulong>();
        private readonly Dictionary<ulong, float>  _lastEscapeAlarm = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float>  _lastMuteRefresh = new Dictionary<ulong, float>();
        private const float MuteRefreshInterval = 45f;

        // event log for the sidebar
        private readonly List<LogEntry> _eventLog = new List<LogEntry>();
        private const int EventLogMax = 7;

        private class LogEntry { public string Time; public string Text; public string Color; }

        private void AddEvent(string text, string color = "0.60 0.60 0.60 1")
        {
            _eventLog.Insert(0, new LogEntry { Time = System.DateTime.UtcNow.ToString("HH:mm"), Text = text, Color = color });
            if (_eventLog.Count > EventLogMax) _eventLog.RemoveAt(_eventLog.Count - 1);
        }

        private Timer _positionTimer;

        private Vector3 yardLocation;
        private Vector3 prisonLocation;
        private float   prisonRadius;

        private const string GrenadeEffect = "assets/prefabs/weapons/f1 grenade/effects/f1grenade_explosion.prefab";
        private const string DeathEffect   = "assets/bundled/prefabs/fx/headshot.prefab";

        private const string PermUse    = "prison.use";
        private const string PermUnjail = "prison.unjail";

        private const string PrisonHoodie = "hoodie";
        private const string PrisonPants  = "pants";
        private const ulong  HoodieSkin   = 2358448958;
        private const ulong  PantsSkin    = 2358451406;

        private const string YardPickaxe     = "pickaxe";
        private const ulong  YardPickaxeSkin = 13078;

        private const string DataFile        = "PrisonData";
        private const string PendingDataFile = "PrisonPending";
        private const string CellDataFile    = "PrisonCells";
        private const string NamesDataFile   = "PrisonNames";
        private const string InvDataFile     = "PrisonInventories";
        private const string ReasonsDataFile = "PrisonReasons";
        private const string TimesDataFile   = "PrisonJailTimes";
        private const string DurationsDataFile = "PrisonSentences";
        private const string OffenseCountsDataFile = "PrisonOffenseCounts";

        private const float  FakeTimerInterval = 300f;
        private const float  YardDuration      = 120f;
        private const string YardCountdownMsg  = "<color=#00ff00>[YARD TIME]</color> ";

        // UI layer names
        private const string UILayer        = "Prison_AdminUI";
        private const string UIReasonLayer  = "Prison_ReasonUI";
        private const string UIPendingLayer = "Prison_PendingUI";
        private const string UIPlayerLayer  = "Prison_PlayerUI";
        private const string UIPaybackLayer = "Prison_PaybackUI";

        // colours (RGBA strings for CUI)
        // bg tones
        private const string C_BG0   = "0.05 0.05 0.05 1";      // deepest bg
        private const string C_BG1   = "0.08 0.08 0.08 1";      // panel bg
        private const string C_BG2   = "0.11 0.11 0.11 1";      // row alt
        private const string C_BG3   = "0.14 0.14 0.14 1";      // hover/header
        private const string C_DIVID = "0.16 0.16 0.16 1";      // divider line

        // Accent headers
        private const string C_RED_HDR   = "0.14 0.05 0.05 1";
        private const string C_RED_EDGE  = "0.40 0.10 0.10 1";
        private const string C_GREEN_HDR = "0.05 0.12 0.05 1";
        private const string C_GREEN_EDGE= "0.18 0.40 0.18 1";

        // Text
        private const string C_TXT_PRI  = "0.88 0.88 0.88 1";   // primary text
        private const string C_TXT_SEC  = "0.65 0.65 0.65 1";   // secondary text (reason, duration)
        private const string C_TXT_DIM  = "0.52 0.52 0.52 1";   // column headers / labels
        private const string C_TXT_HDR  = "0.96 0.96 0.96 1";   // header text

        // Semantic colours
        private const string C_GREEN  = "0.28 0.62 0.18 1";
        private const string C_YELLOW = "0.62 0.52 0.14 1";
        private const string C_RED    = "0.68 0.18 0.18 1";
        private const string C_ORANGE = "0.55 0.32 0.06 1";
        private const string C_BLUE   = "0.42 0.62 0.90 1";

        // row accent bars
        private const string C_ACCENT_YARD    = "0.18 0.42 0.12 1";
        private const string C_ACCENT_ONLINE  = "0.18 0.30 0.55 1";
        private const string C_ACCENT_OFFLINE = "0.20 0.20 0.20 1";
        private const string C_ACCENT_FREE    = "0.18 0.42 0.12 1";

        // Buttons
        private const string C_BTN_YARD  = "0.08 0.20 0.06 1";
        private const string C_BTN_FREE  = "0.22 0.07 0.07 1";
        private const string C_BTN_ABUSE = "0.18 0.10 0.02 1";
        private const string C_BTN_JAIL  = "0.22 0.07 0.07 1";
        private const string C_BTN_GRAY  = "0.10 0.10 0.10 1";
        private const string C_BTN_BLUE  = "0.08 0.18 0.32 1";
        private const string C_BTN_GREEN = "0.06 0.18 0.06 1";

        private static readonly string[] PresetReasons =
        {
            "Toxicity", "Exploiting",
            "Racism", "Admin Jailed", "5Head Abuse"
        };

        private class SavedItem
        {
            public string   shortname;
            public int      amount;
            public ulong    skin;
            public SlotType slot;
        }

        private class SerialVector3
        {
            public float x, y, z;
            public SerialVector3() {}
            public SerialVector3(Vector3 v) { x = v.x; y = v.y; z = v.z; }
            public Vector3 ToVector3() => new Vector3(x, y, z);
        }

        private enum SlotType { Wear, Main, Belt }

        // init / load

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            permission.RegisterPermission(PermUnjail, this);
            jailedPlayers    = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, Vector3>>(DataFile)    ?? new Dictionary<ulong, Vector3>();
            jailedUIDs.AddRange(jailedPlayers.Keys);
            pendingJails     = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>(PendingDataFile)  ?? new Dictionary<ulong, string>();
            originalNames    = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>(NamesDataFile)    ?? new Dictionary<ulong, string>();
            savedInventories = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, List<SavedItem>>>(InvDataFile) ?? new Dictionary<ulong, List<SavedItem>>();
            jailReasons      = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, string>>(ReasonsDataFile)  ?? new Dictionary<ulong, string>();
            jailStartTimes   = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, long>>(TimesDataFile)      ?? new Dictionary<ulong, long>();
            jailDurations    = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, long>>(DurationsDataFile)  ?? new Dictionary<ulong, long>();
            jailOffenseCounts = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, Dictionary<string, int>>>(OffenseCountsDataFile) ?? new Dictionary<ulong, Dictionary<string, int>>();
            LoadCells();
        }

        private void OnServerInitialized()
        {
            LoadConfigValues();
            SaveData();
            if (jailedUIDs.Count > 0) StartPositionTimer();
            timer.Every(120f, AutoYardTime);
        }

        private readonly HashSet<ulong> _playersWithUI = new HashSet<ulong>();

        private void Unload()
        {
            _positionTimer?.Destroy();
            _positionTimer = null;
            foreach (ulong uid in _playersWithUI)
            {
                BasePlayer p = BasePlayer.FindByID(uid);
                if (p == null || !p.IsConnected) continue;
                CuiHelper.DestroyUi(p, UILayer);
                CuiHelper.DestroyUi(p, UIReasonLayer);
                CuiHelper.DestroyUi(p, UIPendingLayer);
                CuiHelper.DestroyUi(p, UIPlayerLayer);
                CuiHelper.DestroyUi(p, UIPaybackLayer);
            }
            _playersWithUI.Clear();
        }

        // position timer

        private void StartPositionTimer()
        {
            if (_positionTimer != null) return;
            _positionTimer = timer.Every(0.25f, CheckJailedPlayers);
        }

        private void StopPositionTimerIfEmpty()
        {
            if (jailedUIDs.Count > 0) return;
            _positionTimer?.Destroy();
            _positionTimer = null;
        }

        // cells

        private void LoadCells()
        {
            var raw = Interface.Oxide.DataFileSystem.ReadObject<List<SerialVector3>>(CellDataFile);
            cellLocations = raw != null ? raw.Select(s => s.ToVector3()).ToList() : new List<Vector3>();
        }

        private void SaveCells() =>
            Interface.Oxide.DataFileSystem.WriteObject(CellDataFile, cellLocations.Select(v => new SerialVector3(v)).ToList());

        private Vector3 PickCell() =>
            cellLocations.Count == 0 ? prisonLocation : cellLocations[UnityEngine.Random.Range(0, cellLocations.Count)];

        [ChatCommand("addcell")]
        private void AddCellCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            cellLocations.Add(player.transform.position);
            SaveCells();
            player.ChatMessage($"<color=#00ff00>Cell #{cellLocations.Count} added at {FormatVec(player.transform.position)}.</color>");
        }

        [ChatCommand("removecell")]
        private void RemoveCellCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            if (cellLocations.Count == 0) { player.ChatMessage("No cells defined."); return; }
            if (args.Length != 1 || !int.TryParse(args[0], out int index) || index < 1 || index > cellLocations.Count)
            { player.ChatMessage($"Usage: /removecell <1-{cellLocations.Count}>"); return; }
            cellLocations.RemoveAt(index - 1);
            SaveCells();
            player.ChatMessage($"<color=#ffaa00>Cell #{index} removed. {cellLocations.Count} remaining.</color>");
        }

        [ChatCommand("listcells")]
        private void ListCellsCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            if (cellLocations.Count == 0) { player.ChatMessage("No cells defined yet."); return; }
            player.ChatMessage($"<color=#ffaa00>=== Prison Cells ({cellLocations.Count}) ===</color>");
            for (int i = 0; i < cellLocations.Count; i++)
                player.ChatMessage($"  <color=#ffffff>#{i + 1}</color> — {FormatVec(cellLocations[i])}");
        }

        [ChatCommand("clearcells")]
        private void ClearCellsCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            cellLocations.Clear();
            SaveCells();
            player.ChatMessage("<color=#ff0000>All cells cleared.</color>");
        }

        private string FormatVec(Vector3 v) => $"({v.x:F0},{v.y:F0},{v.z:F0})";

        // yard

        private readonly string[] YardStartMessages =
        {
            "Congratulations! You've earned 120 seconds of fresh air. Try not to cry.",
            "Yard time granted. Don't get too used to it, shitter.",
            "You may now pace in a slightly larger area. Living the dream.",
            "Enjoy the outdoors. Your cell misses you already.",
            "120 seconds of freedom. Make it count (you won't).",
        };

        private readonly string[] MiningNagMessages =
        {
            "<color=#ffaa00>[WARDEN]</color> Stop standing around. Mine the rocks.",
            "<color=#ffaa00>[WARDEN]</color> You're here to work, not sightsee. Hit the rocks.",
            "<color=#ffaa00>[WARDEN]</color> Those rocks won't mine themselves, shitter.",
            "<color=#ffaa00>[WARDEN]</color> We're watching you. MINE.",
            "<color=#ffaa00>[WARDEN]</color> Your sentence gets longer every second you're not mining.",
            "<color=#ffaa00>[WARDEN]</color> The rocks. Hit them. Now.",
            "<color=#ffaa00>[WARDEN]</color> This is your only job. Don't mess it up.",
        };

        [ChatCommand("setyard")]
        private void SetYardCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            yardLocation = player.transform.position;
            Config["Yard", "X"] = yardLocation.x; Config["Yard", "Y"] = yardLocation.y; Config["Yard", "Z"] = yardLocation.z;
            SaveConfig();
            player.ChatMessage($"<color=#00ff00>Yard location set to {FormatVec(yardLocation)}.</color>");
        }

        [ChatCommand("yard")]
        private void YardCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUse)) return;
            if (args.Length != 1) { admin.ChatMessage("Usage: /yard <player|steamid>"); return; }
            BasePlayer target = FindPlayer(args[0]);
            if (target == null) { admin.ChatMessage("Player not found."); return; }
            if (!IsJailed(target)) { admin.ChatMessage("Not jailed."); return; }
            if (yardActive.Contains(target.userID)) { admin.ChatMessage("Already at yard."); return; }
            StartYardTime(target);
            admin.ChatMessage($"<color=#00ff00>Yard time started for {GetDisplayName(target.userID)}.</color>");
        }

        [ChatCommand("yardall")]
        private void YardAllCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUse)) return;
            int count = 0;
            foreach (ulong uid in jailedUIDs.ToList())
            {
                if (yardActive.Contains(uid)) continue;
                BasePlayer p = BasePlayer.FindByID(uid);
                if (p == null || !p.IsConnected) continue;
                StartYardTime(p); count++;
            }
            if (count == 0) { admin.ChatMessage("Nobody to send - no jailed players online or all already at yard."); return; }
            admin.ChatMessage($"<color=#00ff00>Yard time started for {count} prisoner(s).</color>");
            Server.Broadcast($"<color=#ff5555>[5Heads Arena]</color> Yard time for {count} prisoner(s), enjoy it while it lasts.");
        }

        private void StartYardTime(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !IsJailed(player)) return;
            yardActive.Add(player.userID);
            AddEvent($"{GetDisplayName(player.userID)} sent to yard", "0.55 0.40 0.10 1");
            string msg = YardStartMessages[UnityEngine.Random.Range(0, YardStartMessages.Length)];
            player.ChatMessage($"{YardCountdownMsg}{msg}");
            timer.Once(2f, () => { if (player == null || !player.IsConnected) return; player.ChatMessage("<color=#ffaa00>[WARDEN]</color> You're on rock duty till we call you back. Get mining."); });
            teleporting.Add(player.userID);
            NextTick(() =>
            {
                if (player == null || !player.IsConnected) { teleporting.Remove(player?.userID ?? 0); yardActive.Remove(player?.userID ?? 0); return; }
                player.SetParent(null, true, true);
                player.Teleport(yardLocation);
                teleporting.Remove(player.userID);
                GiveItem(player, YardPickaxe, YardPickaxeSkin, player.inventory.containerBelt);
            });
            int nagIndex = 0;
            timer.Repeat(30f, (int)(YardDuration / 30f), () =>
            {
                if (player == null || !player.IsConnected || !IsJailed(player) || !yardActive.Contains(player.userID)) return;
                player.ChatMessage(MiningNagMessages[nagIndex % MiningNagMessages.Length]);
                nagIndex++;
            });
            RunYardCountdown(player, (int)YardDuration);
        }

        private void RunYardCountdown(BasePlayer player, int totalSeconds)
        {
            int remaining = totalSeconds;
            timer.Repeat(1f, totalSeconds, () =>
            {
                if (player == null || !player.IsConnected || !IsJailed(player) || !yardActive.Contains(player.userID))
                { yardActive.Remove(player?.userID ?? 0); return; }
                remaining--;
                switch (remaining)
                {
                    case 30: player.ChatMessage("<color=#ffaa00>30 seconds left.</color>"); break;
                    case 10: player.ChatMessage("<color=#ff8800>10 seconds. Start walking back, shitter.</color>"); break;
                    case 5:  player.ChatMessage("<color=#ff4400>5...</color>"); break;
                    case 3:  player.ChatMessage("<color=#ff0000>3...</color>"); break;
                    case 2:  player.ChatMessage("<color=#ff0000>2...</color>"); break;
                    case 1:  player.ChatMessage("<color=#ff0000>1... see you never.</color>"); break;
                }
                if (remaining > 0) return;
                yardActive.Remove(player.userID);
                StripYardPickaxe(player);
                EquipPrisonUniform(player);
                player.ChatMessage("<color=#ff0000>[YARD TIME OVER]</color> Back in your box.");
                ForceReturnToPrison(player);
            });
        }

        private void OnDoorOpened(Door door, BaseEntity.RPCMessage msg)
        {
            BasePlayer player = msg.player;
            if (player == null || !IsJailed(player)) return;
            door.SetOpen(false, true);
            player.ChatMessage("<color=#ff0000>The door is locked. Sit down.</color>");
        }

        private void OnDoorOpened(Door door, BasePlayer player)
        {
            if (player == null || !IsJailed(player)) return;
            door.SetOpen(false, true);
            player.ChatMessage("<color=#ff0000>The door is locked. Sit down.</color>");
        }

        // anti-decay: protect prison stone/metal/HQM blocks and doors only
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info?.damageTypes == null) return null;
            if (info.damageTypes.Get(Rust.DamageType.Decay) <= 0f) return null;

            bool protect = entity is Door;
            if (entity is BuildingBlock block)
            {
                protect = block.grade == BuildingGrade.Enum.Stone
                    || block.grade == BuildingGrade.Enum.Metal
                    || block.grade == BuildingGrade.Enum.TopTier;
            }
            if (!protect) return null;

            float radiusSqr = prisonRadius * prisonRadius;
            Vector3 pos = entity.transform.position;
            bool inPrison = (pos - prisonLocation).sqrMagnitude <= radiusSqr
                || cellLocations.Any(cell => (pos - cell).sqrMagnitude <= radiusSqr);
            if (!inPrison) return null;

            info.damageTypes.Scale(Rust.DamageType.Decay, 0f);
            return null;
        }

        // position checks / escape

        private void CheckJailedPlayers()
        {
            // Reverse iteration safely allows UnjailPlayer() below to remove the current UID
            // without allocating a ToList() snapshot every 0.25s pass.
            float prisonRadiusSqr = prisonRadius * prisonRadius;
            float yardRadius = prisonRadius * 3f;
            float yardRadiusSqr = yardRadius * yardRadius;

            for (int i = jailedUIDs.Count - 1; i >= 0; i--)
            {
                ulong uid = jailedUIDs[i];

                BasePlayer player = BasePlayer.FindByID(uid);
                if (player == null || !player.IsConnected) continue;

                // sentence served? let them go before doing anything else this tick
                if (jailDurations.TryGetValue(uid, out long sentenceSecs) && sentenceSecs > 0)
                {
                    long start = jailStartTimes.TryGetValue(uid, out long st) ? st : UnixNow();
                    if (UnixNow() - start >= sentenceSecs)
                    {
                        UnjailPlayer(player, true);
                        continue;
                    }
                }

                // self-heal: native "mute" occasionally doesn't take (dropped on connect,
                // reload, etc.) and nothing else tells us it failed, so just keep
                // re-asserting it cheaply for anyone still flagged as jailed
                float nowMute  = UnityEngine.Time.realtimeSinceStartup;
                float lastMute = _lastMuteRefresh.TryGetValue(uid, out float lm) ? lm : 0f;
                if (nowMute - lastMute >= MuteRefreshInterval)
                {
                    _lastMuteRefresh[uid] = nowMute;
                    MutePlayer(player);
                }
                if (_pendingRejail.Contains(uid))
                { _pendingRejail.Remove(uid); teleporting.Remove(uid); ForceReturnToPrison(player); continue; }
                if (yardActive.Contains(uid))
                {
                    if ((player.transform.position - yardLocation).sqrMagnitude > yardRadiusSqr)
                        ForceReturnToYard(player);
                    continue;
                }
                Vector3 cell    = GetPlayerCell(player);
                bool    outside = (player.transform.position - cell).sqrMagnitude > prisonRadiusSqr;
                if (outside)
                {
                    float now  = UnityEngine.Time.realtimeSinceStartup;
                    float last = _lastEscapeAlarm.ContainsKey(uid) ? _lastEscapeAlarm[uid] : 0f;
                    if (now - last >= 30f)
                    {
                        _lastEscapeAlarm[uid] = now;
                        escapeAlarmActive.Add(uid);
                        TriggerEscapeAlarm(player);
                    }
                    ForceReturnToPrison(player);
                }
                else { escapeAlarmActive.Remove(uid); }
            }
        }

        private void TriggerEscapeAlarm(BasePlayer escapee)
        {
            string name = GetDisplayName(escapee.userID);
            AddEvent($"{name} escape attempt!", "0.70 0.30 0.06 1");
            Server.Broadcast($"<color=#ff5555>[5Heads Arena]</color> <color=#ffffff>{name}</color> is making a break for it!");
            timer.Repeat(0.35f, 6, () =>
            {
                if (!escapeAlarmActive.Contains(escapee.userID)) return;
                foreach (BasePlayer p in BasePlayer.activePlayerList)
                {
                    if (p?.net?.connection == null) continue;
                    var fx = new Effect();
                    fx.Init(Effect.Type.Generic, prisonLocation, Vector3.zero);
                    fx.pooledString = "assets/bundled/prefabs/deepsea/deepsea_wipealarm.prefab";
                    EffectNetwork.Send(fx, p.net.connection);
                }
            });
        }

        private void AutoYardTime()
        {
            foreach (ulong uid in jailedUIDs.ToList())
            {
                if (yardActive.Contains(uid)) continue;
                BasePlayer p = BasePlayer.FindByID(uid);
                if (p == null || !p.IsConnected) continue;
                StartYardTime(p);
            }
        }

        private Vector3 GetPlayerCell(BasePlayer player)
        {
            if (playerCells.TryGetValue(player.userID, out Vector3 cell)) return cell;
            cell = PickCell();
            playerCells[player.userID] = cell;
            return cell;
        }

        // chat / console commands

        [ChatCommand("setprison")]
        private void SetPrisonCommand(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, PermUse)) return;
            prisonLocation = player.transform.position;
            Config["Prison", "X"] = prisonLocation.x; Config["Prison", "Y"] = prisonLocation.y; Config["Prison", "Z"] = prisonLocation.z;
            SaveConfig();
            player.ChatMessage("<color=#00ff00>Fallback prison location set.</color>");
        }

        [ChatCommand("prison")]
        private void PrisonCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUse)) return;
            if (args.Length == 0) { OpenPrisonUI(admin, 0, 0); return; }
            if (args.Length > 2) { admin.ChatMessage("Usage: /prison  OR  /prison <player|steamid> [reason]"); return; }
            string reason = args.Length == 2 ? args[1] : "Unlucky Shitter";
            BasePlayer target = FindPlayer(args[0]);
            if (target != null) { JailPlayer(target, reason); return; }
            if (ulong.TryParse(args[0], out ulong offlineId))
            {
                if (jailedPlayers.ContainsKey(offlineId))  { admin.ChatMessage("Already jailed."); return; }
                if (pendingJails.ContainsKey(offlineId))   { admin.ChatMessage("Already pending."); return; }
                pendingJails[offlineId] = reason;
                SavePendingData();
                admin.ChatMessage($"<color=#ffaa00>{offlineId} will be jailed on connect.</color>");
                return;
            }
            admin.ChatMessage("Player not found. Use SteamID64 for offline jailing.");
        }

        [ConsoleCommand("prison")]
        private void PrisonConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 1) return;
            // Internal RCON moderation may supply an explicit temporary sentence.
            // The optional flag is removed from the reason visible to players.
            var reasonParts = arg.Args.Skip(1).Select(a => (string)a).ToList();
            long? overrideSeconds = null;
            if (reasonParts.Count > 0)
            {
                string lastArg = reasonParts[reasonParts.Count - 1];
                const string durationFlag = "--duration-minutes=";
                if (lastArg.StartsWith(durationFlag, System.StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(lastArg.Substring(durationFlag.Length), out long mins)
                    && mins >= 0 && mins <= 525600)
                {
                    overrideSeconds = mins * 60L;
                    reasonParts.RemoveAt(reasonParts.Count - 1);
                }
            }
            string reason = reasonParts.Count > 0 ? string.Join(" ", reasonParts) : "Rule Violation";
            BasePlayer target = FindPlayer((string)arg.Args[0]);
            if (target != null) { JailPlayer(target, reason, overrideSeconds); return; }
            if (ulong.TryParse((string)arg.Args[0], out ulong offlineId))
            {
                if (jailedPlayers.ContainsKey(offlineId)) return;
                pendingJails[offlineId] = reason;
                SavePendingData();
            }
        }

        [ConsoleCommand("unjail")]
        private void UnjailConsoleCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            BasePlayer target = BasePlayer.FindByID(targetId) ?? BasePlayer.FindSleeping(targetId);
            if (target != null) { UnjailPlayer(target); return; }
            if (jailedPlayers.ContainsKey(targetId))
            {
                UnmutePlayer(targetId);
                jailedPlayers.Remove(targetId); jailedUIDs.Remove(targetId); playerCells.Remove(targetId);
                jailReasons.Remove(targetId); jailStartTimes.Remove(targetId); pendingJails.Remove(targetId);
                jailDurations.Remove(targetId);
                SaveData(); SavePendingData(); SaveReasonsData(); SaveTimesData(); SaveDurationsData();
                StopPositionTimerIfEmpty();
            }
        }

        [ChatCommand("unjail")]
        private void UnjailCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUnjail)) return;
            if (args.Length != 1) { admin.ChatMessage("Usage: /unjail <player|steamid>"); return; }
            if (ulong.TryParse(args[0], out ulong offlineId) && pendingJails.ContainsKey(offlineId))
            { pendingJails.Remove(offlineId); SavePendingData(); admin.ChatMessage($"<color=#00ff00>Pending jail for {offlineId} cancelled.</color>"); return; }
            BasePlayer target = FindPlayer(args[0]);
            if (target == null) { admin.ChatMessage("Player not found."); return; }
            UnjailPlayer(target);
        }

        // open to everyone (no permission check) — lets a jailed player check their own time
        [ChatCommand("timeleft")]
        private void TimeLeftCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsJailed(player)) { player.ChatMessage("<color=#ffaa00>[5Heads Arena]</color> You're not jailed."); return; }
            player.ChatMessage($"<color=#ffaa00>[5Heads Arena]</color>{DescribeRemainingForPlayer(player.userID)}");
        }

        [ChatCommand("jailpending")]
        private void JailPendingCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUse)) return;
            if (pendingJails.Count == 0) { admin.ChatMessage("No pending offline jails."); return; }
            admin.ChatMessage("<color=#ffaa00>=== Pending Offline Jails ===</color>");
            foreach (var kvp in pendingJails)
                admin.ChatMessage($"  <color=#ffffff>{kvp.Key}</color> — {kvp.Value}");
        }

        // cross-plugin API — lets another plugin (e.g. VoiceMonitor, on a detected slur) jail a
        // player through Prison's normal flow, so the reason still goes through Sentences/
        // Escalation like any admin-issued jail. Safe to call repeatedly for the same incident:
        // no-ops if the player is already jailed instead of resetting their sentence clock or
        // double-counting the offense.
        [HookMethod("ApiJailPlayer")]
        public void ApiJailPlayer(BasePlayer player, string reason = "Unlucky Shitter")
        {

            if (player == null)
            {
                Puts("[Prison] ApiJailPlayer called with a null player — the caller's BasePlayer lookup failed. Nothing to do.");
                return;
            }

            if (IsJailed(player))
            {
                Puts($"[Prison] ApiJailPlayer called for {player.displayName} ({player.userID}) but they're already marked jailed in PrisonData — skipping so their sentence/strike count doesn't get reset. If they're NOT actually sitting in prison right now, that means stale jail data is stuck for this SteamID; run /unjail {player.userID} once to clear it, then try again.");
                return;
            }

            Puts($"[Prison] ApiJailPlayer: jailing {player.displayName} ({player.userID}), reason={reason}, connected={player.IsConnected}, sleeping={player.IsSleeping()}");
            JailPlayer(player, reason);
        }

        // jail / unjail logic

        private void JailPlayer(BasePlayer player, string reason = "Unlucky Shitter", long? overrideSeconds = null)
        {
            if (player == null) return;
            if (!originalNames.ContainsKey(player.userID))
            {
                originalNames[player.userID] = player.displayName;
                Interface.Oxide.DataFileSystem.WriteObject(NamesDataFile, originalNames);
            }
            string displayName = originalNames[player.userID];
            jailedPlayers[player.userID]  = player.transform.position;
            if (!jailedUIDs.Contains(player.userID)) jailedUIDs.Add(player.userID);
            playerCells[player.userID]    = PickCell();
            jailReasons[player.userID]    = reason;
            jailStartTimes[player.userID] = UnixNow();
            jailDurations[player.userID]  = overrideSeconds ?? GetSentenceSeconds(player.userID, reason);
            SaveData(); SaveReasonsData(); SaveTimesData(); SaveDurationsData();
            StartPositionTimer();
            // native "mute" silently no-ops if it fires before the connection is fully
            // settled (or if the target turned out to be an offline sleeper) — delay it
            // a beat and re-check, and CheckJailedPlayers() will keep re-asserting it below
            timer.Once(0.5f, () => { if (player != null && player.IsConnected) MutePlayer(player); });
            AddEvent($"{displayName} jailed — {reason}", "0.68 0.18 0.18 1");
            if (player.IsConnected && player.net?.connection != null && !player.IsSleeping())
            {
                // run before SaveInventory - pulls them out of any gamemode kit first
                RemoveFromGamemodes(player);
                SaveInventory(player);
                EquipPrisonUniform(player);
                RenamePlayer(player);
                if (IsInRace(player))
                {
                    TryLeaveRace(player);
                    _pendingRejail.Add(player.userID);
                    timer.Once(1f, () =>
                    {
                        if (player == null || !player.IsConnected) return;
                        _pendingRejail.Remove(player.userID);
                        HardDismount(player);
                        ForceReturnToPrison(player);
                    });
                }
                else NextTick(() => ForceReturnToPrison(player));
                player.ChatMessage($"<color=#ff0000>You're jailed {DescribeSentenceForPlayer(player.userID)}. Reason: {reason}</color>");
                StartFakeReleaseTimer(player);
                StartHeliSound(player);
                StartDeathSound(player);
            }
            // One formatted announcement for all successful jails (UI, command and bot).
            // The player's original name is used because the jail system renames them.
            string cleanName = CleanAnnouncementText(displayName);
            string cleanReason = CleanAnnouncementText(reason);
            long sentence = jailDurations[player.userID];
            string lengthText = sentence <= 0 ? "permanently" : "for " + FormatAnnouncementDuration(sentence);
            Server.Broadcast($"<color=#16c36a>[5Heads Arena]</color> <color=#ffcd36>{cleanName}({player.userID})</color> locked up {lengthText} - <color=#ffcd36>Reason: {cleanReason}</color>");
            ReportPrisonToArenaBot(player, displayName, reason);
        }

        private void UnjailPlayer(BasePlayer player, bool sentenceCompleted = false)
        {
            if (!jailedPlayers.TryGetValue(player.userID, out Vector3 releasePos))
            { player.ChatMessage("Player is not jailed."); return; }
            string name = GetDisplayName(player.userID);
            jailedPlayers.Remove(player.userID);  jailedUIDs.Remove(player.userID);
            playerCells.Remove(player.userID);    jailReasons.Remove(player.userID);
            jailStartTimes.Remove(player.userID); teleporting.Remove(player.userID);
            fakeTimerActive.Remove(player.userID); heliActive.Remove(player.userID);
            deathActive.Remove(player.userID);    yardActive.Remove(player.userID);
            escapeAlarmActive.Remove(player.userID); _pendingRejail.Remove(player.userID);
            _lastEscapeAlarm.Remove(player.userID);
            _lastMuteRefresh.Remove(player.userID);
            jailDurations.Remove(player.userID);
            SaveData(); SaveReasonsData(); SaveTimesData(); SaveDurationsData();
            player.metabolism.radiation_poison.SetValue(0f); player.metabolism.radiation_level.SetValue(0f);
            player.metabolism.bleeding.SetValue(0f); player.metabolism.calories.SetValue(500f);
            player.metabolism.hydration.SetValue(250f);
            player.SendNetworkUpdateImmediate();
            RestoreName(player); RestoreInventory(player); UnmutePlayer(player);
            AddEvent($"{name} released", "0.22 0.55 0.14 1");
            // Match jail notices: one branded public release announcement.
            string releaseDescription = sentenceCompleted ? "sentence completed" : "released by admin";
            Server.Broadcast($"<color=#16c36a>[5Heads Arena]</color> <color=#ffcd36>{CleanAnnouncementText(name)}({player.userID})</color> has been released - {releaseDescription}");
            // only send to lobby if they were actually mid-gamemode
            bool wasInGamemode = RemoveFromGamemodes(player);
            Vector3 destination = releasePos;
            if (wasInGamemode && TryGetCoreLobbySpawn(out Vector3 lobbySpawn)) destination = lobbySpawn;
            NextTick(() => { if (player == null || !player.IsConnected) return; player.Teleport(destination); player.ChatMessage("<color=#00ff00>You are free.</color>"); });
            StopPositionTimerIfEmpty();
        }

        // player events

        private void OnPlayerConnected(BasePlayer player)
        {
            if (pendingJails.TryGetValue(player.userID, out string reason))
            {
                pendingJails.Remove(player.userID); SavePendingData();
                timer.Once(2f, () => { if (player == null || !player.IsConnected) return; JailPlayer(player, reason); });
                return;
            }
            if (!IsJailed(player)) return;
            // sentence may have expired entirely while they were offline — let them straight out
            // instead of re-jailing/re-muting them just to release them a moment later
            if (jailDurations.TryGetValue(player.userID, out long durSecs) && durSecs > 0)
            {
                long start = jailStartTimes.TryGetValue(player.userID, out long st) ? st : UnixNow();
                if (UnixNow() - start >= durSecs)
                {
                    timer.Once(1f, () =>
                    {
                        if (player == null || !player.IsConnected || !IsJailed(player)) return;
                        UnjailPlayer(player, true);
                        player.ChatMessage("<color=#00ff00>Your sentence was served while you were away. You're free.</color>");
                    });
                    return;
                }
            }
            playerCells[player.userID] = PickCell();
            if (!savedInventories.ContainsKey(player.userID)) SaveInventory(player);
            timer.Once(1f, () => { if (player == null || !player.IsConnected) return; EquipPrisonUniform(player); RenamePlayer(player); MutePlayer(player); });
            StartPositionTimer();
            player.ChatMessage($"<color=#ff0000>Welcome back to prison, shitter.{DescribeRemainingForPlayer(player.userID)}</color>");
            NextTick(() => ForceReturnToPrison(player));
            StartFakeReleaseTimer(player); StartHeliSound(player); StartDeathSound(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason) { }

        private void OnPlayerDeath(BasePlayer player, HitInfo info)
        {
            if (player == null || !IsJailed(player)) return;
            timer.Once(1f, () => { if (player == null || !player.IsConnected) return; player.RespawnAt(GetPlayerCell(player), Quaternion.identity); });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (!IsJailed(player)) return;
            timer.Once(0.5f, () => { if (player == null || !player.IsConnected) return; EquipPrisonUniform(player); RenamePlayer(player); ForceReturnToPrison(player); });
        }

        // ui — console commands

        [ConsoleCommand("prison.closeui")]
        private void UIClose(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, UILayer);
            CuiHelper.DestroyUi(p, UIReasonLayer);
            CuiHelper.DestroyUi(p, UIPendingLayer);
            CuiHelper.DestroyUi(p, UIPlayerLayer);
            CuiHelper.DestroyUi(p, UIPaybackLayer);
            _playersWithUI.Remove(p.userID);
        }

        [ConsoleCommand("prison.refresh")]
        private void UIRefresh(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null || !permission.UserHasPermission(p.UserIDString, PermUse)) return;
            int jp = arg.Args != null && arg.Args.Length > 0 && int.TryParse((string)arg.Args[0], out int j) ? j : 0;
            int fp = arg.Args != null && arg.Args.Length > 1 && int.TryParse((string)arg.Args[1], out int f) ? f : 0;
            OpenPrisonUI(p, jp, fp);
        }

        [ConsoleCommand("prison.reason_ui")]
        private void UIReasonPicker(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null || IsJailed(target)) return;
            OpenReasonUI(admin, target);
        }

        [ConsoleCommand("prison.jail_confirm")]
        private void UIJailConfirm(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            string reason = string.Join(" ", arg.Args.Skip(1).Select(a => (string)a));
            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && !IsJailed(target)) JailPlayer(target, reason);
            CuiHelper.DestroyUi(admin, UIReasonLayer);
            OpenPrisonUI(admin, 0, 0);
        }

        [ConsoleCommand("prison.reason_cancel")]
        private void UIReasonCancel(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, UIReasonLayer);
        }

        [ConsoleCommand("prison.unjail_ui")]
        private void UIUnjail(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player();
            if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUnjail)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            BasePlayer target = BasePlayer.FindByID(targetId) ?? BasePlayer.FindSleeping(targetId);
            if (target != null) { UnjailPlayer(target); }
            else if (jailedPlayers.ContainsKey(targetId))
            {
                string name = GetDisplayName(targetId);
                UnmutePlayer(targetId);
                jailedPlayers.Remove(targetId); jailedUIDs.Remove(targetId); playerCells.Remove(targetId);
                jailReasons.Remove(targetId); jailStartTimes.Remove(targetId); jailDurations.Remove(targetId);
                SaveData(); SaveReasonsData(); SaveTimesData(); SaveDurationsData();
                StopPositionTimerIfEmpty();
                AddEvent($"{name} released (offline)", "0.22 0.55 0.14 1");
            }
            OpenPrisonUI(admin, 0, 0);
        }

        [ConsoleCommand("prison.yard_ui")]
        private void UIYardOne(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target != null && IsJailed(target) && !yardActive.Contains(targetId)) StartYardTime(target);
            OpenPrisonUI(admin, 0, 0);
        }

        [ConsoleCommand("prison.yardall_ui")]
        private void UIYardAll(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            int count = 0;
            foreach (ulong uid in jailedUIDs.ToList())
            {
                if (yardActive.Contains(uid)) continue;
                BasePlayer p = BasePlayer.FindByID(uid);
                if (p == null || !p.IsConnected) continue;
                StartYardTime(p); count++;
            }
            if (count > 0) Server.Broadcast($"<color=#ff5555>[5Heads Arena]</color> Yard time granted to {count} prisoner(s).");
            OpenPrisonUI(admin, 0, 0);
        }

        // payback ui

        private static readonly string[,] PaybackCards =
        {
            { "BSOD","bs" },{ "NAKED","nk" },{ "ROCKETMAN","rm" },{ "RADIATION","rad" },
            { "SHOCKER","sh" },{ "INTERROGATE","sack" },{ "SPINBOT","spin" },{ "EARTHQUAKE","quake" },
            { "DRUNK","dk" },{ "FLASHBANG","fl" },{ "FINGERBANG","fb" },{ "TRAIN","tr" },
            { "LAG SWITCH","lag" },{ "MINEFIELD","mine" },{ "SLOW BURN","sb" },{ "CHICKENS","ch" },
            { "SKIBIDI","skibidi" },{ "BONK","bk" },{ "PACIFISM","pf" },{ "BUTTERFINGERS","bf" },
            { "INSTANT KARMA","in" },{ "DUD","dud" },{ "DOG DOO","dog" },{ "HIGHER GROUND","hg" },
            { "THIRSTY","th" },{ "DR NO","dr" },{ "STEAL INV","steal" },{ "PINYATA","pin" },
            { "BURN","bu" },{ "MASOCHIST","ms" },{ "EMOTE","em" },{ "SHARK","jaws" },
            { "NO REST","nr" },{ "WOODS","bear" },{ "POTATO MODE","potato" },{ "HOG WILD","hog" },
            { "SPITROAST","roast" },{ "BUGGED GUN","bug" },{ "VERBAL DIARHEA","shitter" },{ "POOP ROCKET","poop" },
            { "RECYCLE","re" },{ "BATMAN","bat" },{ "RECOIL","recoil" },{ "NO PICKUP","np" },
            { "AIR STRIKE","as" },{ "LOUDFOOT","lf" },{ "STICKY BOMB","c4" },{ "BLOWBACK","bb" },
            { "CANNONBALL","cb" },{ "SNITCH","sn" },{ "BUTTERHOOVES","bh" },{ "SWARM","bees" },
            { "CATAPULT","yeet" },{ "TAXMAN","tax" },{ "CARROT","carrot" },{ "VIEW LOOT","loot" },
            { "POOP ROCKET 2","pr" },{ "STICKY BOMB C4","c4" },
        };

        private const int PaybackCardsPerPage = 18;

        [ConsoleCommand("prison.payback_ui")]
        private void UIPaybackOpen(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            if (!jailedPlayers.ContainsKey(targetId)) return;
            int page = arg.Args.Length >= 2 && int.TryParse((string)arg.Args[1], out int pg) ? pg : 0;
            OpenPaybackUI(admin, targetId, page);
        }

        [ConsoleCommand("prison.payback_apply")]
        private void UIPaybackApply(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player();
            if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            if (Payback3 == null) { admin.ChatMessage("<color=#ff5555>[Prison]</color> Payback3 plugin is not loaded."); return; }
            string alias = (string)arg.Args[1];
            int page = arg.Args.Length >= 3 && int.TryParse((string)arg.Args[2], out int pg) ? pg : 0;
            BasePlayer target = BasePlayer.FindByID(targetId);
            if (target == null || !target.IsConnected) { admin.ChatMessage("<color=#ff5555>[Prison]</color> Target must be online for Payback3 cards."); return; }
            ConsoleSystem.Run(ConsoleSystem.Option.Server.FromConnection(admin.net.connection), $"{alias} {targetId}");
            AddEvent($"{alias.ToUpper()} → {GetDisplayName(targetId)}", "0.55 0.32 0.06 1");
            OpenPaybackUI(admin, targetId, page);
        }

        [ConsoleCommand("prison.payback_close")]
        private void UIPaybackClose(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, UIPaybackLayer);
        }

        private void OpenPaybackUI(BasePlayer admin, ulong uid, int page = 0)
        {
            CuiHelper.DestroyUi(admin, UIPaybackLayer);
            _playersWithUI.Add(admin.userID);
            if (Payback3 == null) { admin.ChatMessage("<color=#ff5555>[Prison]</color> Payback3 plugin is not loaded."); return; }
            int totalCards = PaybackCards.GetLength(0);
            int totalPages = (int)System.Math.Ceiling((double)totalCards / PaybackCardsPerPage);
            page = Mathf.Clamp(page, 0, totalPages - 1);
            int startIdx = page * PaybackCardsPerPage;
            int endIdx   = Mathf.Min(startIdx + PaybackCardsPerPage, totalCards);
            bool targetOnline = BasePlayer.FindByID(uid)?.IsConnected ?? false;

            var cu = new CuiElementContainer();
            // Dim overlay — click outside to close
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0.80" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", UIPaybackLayer);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "prison.payback_close" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, UIPaybackLayer);

            string box = UIPaybackLayer + "_box";
            cu.Add(new CuiPanel { Image = { Color = C_BG1 }, RectTransform = { AnchorMin = "0.22 0.05", AnchorMax = "0.78 0.96" } }, UIPaybackLayer, box);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, box);

            // Header
            string hdr = box + "_hdr";
            cu.Add(new CuiPanel { Image = { Color = C_BTN_ABUSE }, RectTransform = { AnchorMin = "0 0.948", AnchorMax = "1 1" } }, box, hdr);
            cu.Add(new CuiPanel { Image = { Color = C_ORANGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.005 1" } }, hdr);
            string onlineTag = targetOnline ? "" : "  [OFFLINE]";
            UILbl(cu, hdr, "0.03 0", "0.70 1", $"PAYBACK3  —  {GetDisplayName(uid).ToUpper()}{onlineTag}", 12, TextAnchor.MiddleLeft, C_TXT_HDR, "robotocondensed-bold.ttf");
            UILbl(cu, hdr, "0.70 0", "0.90 1", $"Page {page + 1}/{totalPages}", 9, TextAnchor.MiddleCenter, C_TXT_SEC);
            cu.Add(new CuiButton { Button = { Color = C_RED, Command = "prison.payback_close" }, RectTransform = { AnchorMin = "0.945 0.10", AnchorMax = "0.998 0.90" }, Text = { Text = "✕", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = C_TXT_HDR, Font = "robotocondensed-bold.ttf" } }, hdr);
            UILbl(cu, box, "0.03 0.912", "0.97 0.945", "Greyed out = prisoner offline.", 9, TextAnchor.MiddleLeft, C_TXT_DIM);

            // Pagination
            bool hasPrev = page > 0; bool hasNext = page < totalPages - 1;
            cu.Add(new CuiButton { Button = { Color = hasPrev ? C_BTN_ABUSE : C_BTN_GRAY, Command = hasPrev ? $"prison.payback_ui {uid} {page - 1}" : "" }, RectTransform = { AnchorMin = "0.03 0.01", AnchorMax = "0.48 0.07" }, Text = { Text = "◀  PREV", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = hasPrev ? "1 0.75 0.35 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, box);
            cu.Add(new CuiButton { Button = { Color = hasNext ? C_BTN_ABUSE : C_BTN_GRAY, Command = hasNext ? $"prison.payback_ui {uid} {page + 1}" : "" }, RectTransform = { AnchorMin = "0.52 0.01", AnchorMax = "0.97 0.07" }, Text = { Text = "NEXT  ▶", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = hasNext ? "1 0.75 0.35 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, box);

            // Card grid
            int count = endIdx - startIdx;
            int cols = 3;
            int rows = (int)System.Math.Ceiling((double)count / cols);
            float gX0 = 0.025f, gX1 = 0.975f, gY0 = 0.075f, gY1 = 0.908f;
            float padX = 0.012f, padY = 0.010f;
            float colW = (gX1 - gX0 - padX * (cols - 1)) / cols;
            float rowH = rows > 0 ? (gY1 - gY0 - padY * (rows - 1)) / rows : 0.1f;
            for (int i = 0; i < count; i++)
            {
                int   cardIdx = startIdx + i;
                string label  = PaybackCards[cardIdx, 0];
                string alias  = PaybackCards[cardIdx, 1];
                int col = i % cols, row = i / cols;
                float x0 = gX0 + col * (colW + padX), x1 = x0 + colW;
                float y1 = gY1 - row * (rowH + padY),  y0 = y1 - rowH;
                string ck = box + "_card" + i;
                cu.Add(new CuiPanel { Image = { Color = "0.10 0.07 0.03 1" }, RectTransform = { AnchorMin = $"{x0:F3} {y0:F3}", AnchorMax = $"{x1:F3} {y1:F3}" } }, box, ck);
                cu.Add(new CuiPanel { Image = { Color = C_ORANGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.025" } }, ck);
                cu.Add(new CuiButton
                {
                    Button = { Color = targetOnline ? "0.45 0.18 0.04 0.85" : "0.16 0.14 0.10 0.85", Command = targetOnline ? $"prison.payback_apply {uid} {alias} {page}" : "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    Text = { Text = label, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = targetOnline ? "1 0.82 0.55 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" }
                }, ck);
            }
            CuiHelper.AddUi(admin, cu);
        }

        // pending ui

        [ConsoleCommand("prison.pending_ui")]
        private void UIPendingOpen(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            OpenPendingUI(admin);
        }

        [ConsoleCommand("prison.pending_cancel_ui")]
        private void UIPendingCancel(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null) return;
            if (arg.Args != null && arg.Args.Length >= 1 && ulong.TryParse((string)arg.Args[0], out ulong cancelId))
            { pendingJails.Remove(cancelId); SavePendingData(); AddEvent($"Pending jail cancelled — {cancelId}", C_TXT_SEC); }
            CuiHelper.DestroyUi(admin, UIPendingLayer);
            OpenPendingUI(admin);
        }

        [ConsoleCommand("prison.pending_close")]
        private void UIPendingClose(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, UIPendingLayer);
        }

        private void OpenPendingUI(BasePlayer admin)
        {
            CuiHelper.DestroyUi(admin, UIPendingLayer);
            _playersWithUI.Add(admin.userID);
            var cu = new CuiElementContainer();
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0.80" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", UIPendingLayer);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "prison.pending_close" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, UIPendingLayer);

            string box = UIPendingLayer + "_box";
            cu.Add(new CuiPanel { Image = { Color = C_BG1 }, RectTransform = { AnchorMin = "0.18 0.10", AnchorMax = "0.82 0.92" } }, UIPendingLayer, box);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, box);

            // Header
            cu.Add(new CuiPanel { Image = { Color = C_BTN_ABUSE }, RectTransform = { AnchorMin = "0 0.930", AnchorMax = "1 1" } }, box);
            cu.Add(new CuiPanel { Image = { Color = C_ORANGE }, RectTransform = { AnchorMin = "0 0.930", AnchorMax = "0.005 1" } }, box);
            UILbl(cu, box, "0.03 0.930", "0.82 1.00", $"PENDING OFFLINE JAILS  ({pendingJails.Count})", 13, TextAnchor.MiddleLeft, C_TXT_HDR, "robotocondensed-bold.ttf");
            cu.Add(new CuiButton { Button = { Color = C_RED, Command = "prison.pending_close" }, RectTransform = { AnchorMin = "0.945 0.933", AnchorMax = "0.998 0.998" }, Text = { Text = "✕", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = C_TXT_HDR, Font = "robotocondensed-bold.ttf" } }, box);

            if (pendingJails.Count == 0)
            {
                UILbl(cu, box, "0 0.40", "1 0.60", "No pending offline jails.", 13, TextAnchor.MiddleCenter, C_TXT_DIM);
            }
            else
            {
                // Column header
                string ph = box + "_ph";
                cu.Add(new CuiPanel { Image = { Color = C_BG3 }, RectTransform = { AnchorMin = "0.02 0.887", AnchorMax = "0.98 0.927" } }, box, ph);
                UILbl(cu, ph, "0.04 0", "0.50 1", "STEAM ID",   9, TextAnchor.MiddleLeft,   C_TXT_DIM);
                UILbl(cu, ph, "0.50 0", "0.78 1", "REASON",     9, TextAnchor.MiddleLeft,   C_TXT_DIM);
                UILbl(cu, ph, "0.78 0", "1.00 1", "ACTION",     9, TextAnchor.MiddleCenter, C_TXT_DIM);

                var entries = pendingJails.ToList(); float rowH2 = 0.088f; float topY = 0.884f;
                for (int i = 0; i < Mathf.Min(entries.Count, 9); i++)
                {
                    ulong  pid     = entries[i].Key;
                    string preason = entries[i].Value;
                    float  py2     = topY - i * rowH2;
                    float  py1     = py2 - rowH2 + 0.004f;
                    string prk     = box + "_pr" + i;
                    cu.Add(new CuiPanel { Image = { Color = i % 2 == 0 ? C_BG1 : C_BG2 }, RectTransform = { AnchorMin = $"0.02 {py1:F4}", AnchorMax = $"0.98 {py2:F4}" } }, box, prk);
                    cu.Add(new CuiPanel { Image = { Color = C_ORANGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.008 1" } }, prk);
                    cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = $"prison.player_ui {pid}" }, RectTransform = { AnchorMin = "0.03 0", AnchorMax = "0.50 1" }, Text = { Text = pid.ToString(), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = C_BLUE, Font = "robotocondensed-regular.ttf" } }, prk);
                    string sr = preason.Length > 18 ? preason.Substring(0, 16) + ".." : preason;
                    UILbl(cu, prk, "0.50 0", "0.78 1", sr, 10, TextAnchor.MiddleLeft, C_TXT_PRI);
                    cu.Add(new CuiButton { Button = { Color = C_BTN_FREE, Command = $"prison.pending_cancel_ui {pid}" }, RectTransform = { AnchorMin = "0.800 0.12", AnchorMax = "0.990 0.88" }, Text = { Text = "CANCEL", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 0.70 0.70 1", Font = "robotocondensed-bold.ttf" } }, prk);
                }
            }
            CuiHelper.AddUi(admin, cu);
        }

        // player info popup

        [ConsoleCommand("prison.player_ui")]
        private void UIPlayerInfo(ConsoleSystem.Arg arg)
        {
            BasePlayer admin = arg.Player(); if (admin == null || !permission.UserHasPermission(admin.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 1) return;
            if (!ulong.TryParse((string)arg.Args[0], out ulong targetId)) return;
            OpenPlayerInfoUI(admin, targetId);
        }

        [ConsoleCommand("prison.player_close")]
        private void UIPlayerClose(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null) return;
            CuiHelper.DestroyUi(p, UIPlayerLayer);
        }

        private void OpenPlayerInfoUI(BasePlayer admin, ulong uid)
        {
            CuiHelper.DestroyUi(admin, UIPlayerLayer);
            _playersWithUI.Add(admin.userID);
            var cu = new CuiElementContainer();
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0.75" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", UIPlayerLayer);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "prison.player_close" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, UIPlayerLayer);

            string box = UIPlayerLayer + "_box";
            cu.Add(new CuiPanel { Image = { Color = C_BG1 }, RectTransform = { AnchorMin = "0.30 0.18", AnchorMax = "0.70 0.86" } }, UIPlayerLayer, box);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, box);

            bool   jailed  = jailedPlayers.ContainsKey(uid);
            bool   online  = BasePlayer.FindByID(uid)?.IsConnected ?? false;
            bool   inYard  = yardActive.Contains(uid);
            bool   pending = pendingJails.ContainsKey(uid);
            string dName   = GetDisplayName(uid);
            string reason  = jailReasons.TryGetValue(uid, out string jr) ? jr : "—";
            string dur     = jailed ? FormatTimeFull(uid) : "—";
            float  hp      = online ? (BasePlayer.FindByID(uid)?.health ?? -1f) : -1f;

            string status      = inYard ? "IN YARD" : jailed ? (online ? "JAILED — ONLINE" : "JAILED — OFFLINE") : pending ? "PENDING JAIL" : online ? "FREE — ONLINE" : "FREE — OFFLINE";
            string statusColor = inYard ? C_GREEN : jailed ? C_RED : pending ? C_YELLOW : C_GREEN;

            // Header
            string hdr = box + "_hdr";
            cu.Add(new CuiPanel { Image = { Color = C_BG3 }, RectTransform = { AnchorMin = "0 0.920", AnchorMax = "1 1" } }, box, hdr);
            cu.Add(new CuiPanel { Image = { Color = C_BLUE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.005 1" } }, hdr);
            UILbl(cu, hdr, "0.04 0", "0.85 1", "PLAYER INFO", 14, TextAnchor.MiddleLeft, C_TXT_HDR, "robotocondensed-bold.ttf");
            cu.Add(new CuiButton { Button = { Color = C_BTN_FREE, Command = "prison.player_close" }, RectTransform = { AnchorMin = "0.942 0.10", AnchorMax = "0.998 0.90" }, Text = { Text = "✕", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = C_TXT_HDR, Font = "robotocondensed-bold.ttf" } }, hdr);

            // Info rows
            float rowTop = 0.908f; float rH = 0.112f;
            PlayerInfoRow(cu, box, rowTop - 0 * rH, "NAME",     dName,          C_TXT_PRI);
            PlayerInfoRow(cu, box, rowTop - 1 * rH, "STEAM ID", uid.ToString(), C_BLUE);
            PlayerInfoRow(cu, box, rowTop - 2 * rH, "STATUS",   status,         statusColor);
            PlayerInfoRow(cu, box, rowTop - 3 * rH, "REASON",   reason,         C_TXT_SEC);
            PlayerInfoRow(cu, box, rowTop - 4 * rH, "TIME",     dur,            C_TXT_SEC);
            string hpTxt = hp >= 0 ? $"{(int)hp} HP" : "—";
            string hpCol = hp > 60 ? C_GREEN : hp > 30 ? C_YELLOW : hp >= 0 ? C_RED : C_TXT_DIM;
            PlayerInfoRow(cu, box, rowTop - 5 * rH, "HEALTH",   hpTxt,          hpCol);

            // Action buttons
            if (jailed)
            {
                bool canYard = online && !inYard;
                bool pbAvail = Payback3 != null;
                cu.Add(new CuiButton { Button = { Color = C_BTN_FREE,  Command = $"prison.unjail_ui {uid}" }, RectTransform = { AnchorMin = "0.03 0.155", AnchorMax = "0.48 0.225" }, Text = { Text = "FREE PLAYER",    FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.75 0.75 1", Font = "robotocondensed-bold.ttf" } }, box);
                cu.Add(new CuiButton { Button = { Color = canYard ? C_BTN_YARD : C_BTN_GRAY, Command = canYard ? $"prison.yard_ui {uid}" : "" }, RectTransform = { AnchorMin = "0.52 0.155", AnchorMax = "0.97 0.225" }, Text = { Text = "SEND TO YARD", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = canYard ? "0.75 1 0.75 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, box);
                cu.Add(new CuiButton { Button = { Color = pbAvail ? C_BTN_ABUSE : C_BTN_GRAY, Command = pbAvail ? $"prison.payback_ui {uid} 0" : "" }, RectTransform = { AnchorMin = "0.03 0.040", AnchorMax = "0.97 0.148" }, Text = { Text = pbAvail ? "⚡  OPEN PAYBACK3 CARD PICKER" : "PAYBACK3 NOT LOADED", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = pbAvail ? "1 0.78 0.35 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, box);
            }
            else if (!pending)
                cu.Add(new CuiButton { Button = { Color = C_BTN_FREE, Command = $"prison.reason_ui {uid}" }, RectTransform = { AnchorMin = "0.03 0.040", AnchorMax = "0.97 0.140" }, Text = { Text = "JAIL PLAYER", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.75 0.75 1", Font = "robotocondensed-bold.ttf" } }, box);
            else
                cu.Add(new CuiButton { Button = { Color = C_BTN_ABUSE, Command = $"prison.pending_cancel_ui {uid}" }, RectTransform = { AnchorMin = "0.03 0.040", AnchorMax = "0.97 0.140" }, Text = { Text = "CANCEL PENDING JAIL", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.82 0.45 1", Font = "robotocondensed-bold.ttf" } }, box);

            CuiHelper.AddUi(admin, cu);
        }

        private void PlayerInfoRow(CuiElementContainer cu, string parent, float yTop, string label, string value, string valueColor)
        {
            float yMin = yTop - 0.104f; string rk = parent + "_pir" + label;
            cu.Add(new CuiPanel { Image = { Color = C_BG2 }, RectTransform = { AnchorMin = $"0.03 {yMin:F4}", AnchorMax = $"0.97 {yTop:F4}" } }, parent, rk);
            UILbl(cu, rk, "0.05 0", "0.38 1", label, 9,  TextAnchor.MiddleLeft, C_TXT_DIM);
            UILbl(cu, rk, "0.38 0", "0.99 1", value, 11, TextAnchor.MiddleLeft, valueColor);
        }

        // reason picker

        private void OpenReasonUI(BasePlayer admin, BasePlayer target)
        {
            CuiHelper.DestroyUi(admin, UIReasonLayer);
            _playersWithUI.Add(admin.userID);
            var cu = new CuiElementContainer();
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0.75" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", UIReasonLayer);

            string box = UIReasonLayer + "_box";
            cu.Add(new CuiPanel { Image = { Color = C_BG1 }, RectTransform = { AnchorMin = "0.30 0.28", AnchorMax = "0.70 0.74" } }, UIReasonLayer, box);

            // Header
            cu.Add(new CuiPanel { Image = { Color = C_RED_HDR }, RectTransform = { AnchorMin = "0 0.886", AnchorMax = "1 1" } }, box);
            cu.Add(new CuiPanel { Image = { Color = C_RED_EDGE }, RectTransform = { AnchorMin = "0 0.886", AnchorMax = "0.005 1" } }, box);
            string tName = target.displayName.Length > 22 ? target.displayName.Substring(0, 20) + ".." : target.displayName;
            UILbl(cu, box, "0.04 0.886", "0.96 1.00", $"SELECT REASON — {tName.ToUpper()}", 11, TextAnchor.MiddleCenter, C_TXT_HDR, "robotocondensed-bold.ttf");

            // Reason buttons — 2 columns, 4 rows
            for (int i = 0; i < PresetReasons.Length; i++)
            {
                int   col  = i % 2;
                int   row  = i / 2;
                float xMin = col == 0 ? 0.04f : 0.52f;
                float xMax = col == 0 ? 0.48f : 0.96f;
                float yMax = 0.862f - row * 0.200f;
                float yMin = yMax - 0.175f;
                cu.Add(new CuiButton
                {
                    Button    = { Color = C_BG3, Command = $"prison.jail_confirm {target.userID} {PresetReasons[i]}" },
                    RectTransform = { AnchorMin = $"{xMin:F2} {yMin:F3}", AnchorMax = $"{xMax:F2} {yMax:F3}" },
                    Text      = { Text = PresetReasons[i].ToUpper(), FontSize = 11, Align = TextAnchor.MiddleCenter, Color = C_TXT_PRI, Font = "robotocondensed-bold.ttf" }
                }, box);
            }

            // Cancel
            cu.Add(new CuiButton { Button = { Color = C_BTN_FREE, Command = "prison.reason_cancel" }, RectTransform = { AnchorMin = "0.04 0.02", AnchorMax = "0.96 0.10" }, Text = { Text = "CANCEL", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.70 0.70 1", Font = "robotocondensed-bold.ttf" } }, box);
            CuiHelper.AddUi(admin, cu);
        }

        // main prison ui

        [ConsoleCommand("prison.page")]
        private void UIPage(ConsoleSystem.Arg arg)
        {
            BasePlayer p = arg.Player(); if (p == null || !permission.UserHasPermission(p.UserIDString, PermUse)) return;
            if (arg.Args == null || arg.Args.Length < 2) return;
            if (!int.TryParse((string)arg.Args[0], out int jailPage) || !int.TryParse((string)arg.Args[1], out int freePage)) return;
            OpenPrisonUI(p, jailPage, freePage);
        }

        private void OpenPrisonUI(BasePlayer admin, int jailPage = 0, int freePage = 0)
        {
            CuiHelper.DestroyUi(admin, UILayer);
            _playersWithUI.Add(admin.userID);
            var cu = new CuiElementContainer();

            // Pagination data
            const int jailPerPage = 6;
            const int freePerPage = 7;
            int jailTotal = jailedUIDs.Count;
            int jailPages = Mathf.Max(1, (int)System.Math.Ceiling((double)jailTotal / jailPerPage));
            jailPage = Mathf.Clamp(jailPage, 0, jailPages - 1);

            var allFree   = BasePlayer.activePlayerList.Where(p => !jailedPlayers.ContainsKey(p.userID)).OrderBy(p => p.displayName).ToList();
            int freeTotal = allFree.Count;
            int freePages = Mathf.Max(1, (int)System.Math.Ceiling((double)freeTotal / freePerPage));
            freePage = Mathf.Clamp(freePage, 0, freePages - 1);

            var jailedPage = jailedUIDs.Skip(jailPage  * jailPerPage).Take(jailPerPage).ToList();
            var freePage_  = allFree   .Skip(freePage  * freePerPage).Take(freePerPage).ToList();

            // Root overlay
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0.92" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, CursorEnabled = true }, "Overlay", UILayer);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "prison.closeui" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, UILayer);

            // Outer panel
            string panel = UILayer + "_p";
            cu.Add(new CuiPanel { Image = { Color = C_BG1 }, RectTransform = { AnchorMin = "0.03 0.03", AnchorMax = "0.97 0.97" } }, UILayer, panel);
            cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = "" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }, Text = { Text = "" } }, panel);

            // Header bar
            string hdr = panel + "_hdr";
            cu.Add(new CuiPanel { Image = { Color = C_RED_HDR }, RectTransform = { AnchorMin = "0 0.944", AnchorMax = "1 1" } }, panel, hdr);
            cu.Add(new CuiPanel { Image = { Color = C_RED_EDGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.003 1" } }, hdr);
            UILbl(cu, hdr, "0.010 0", "0.50 1", "PRISON WARDEN", 14, TextAnchor.MiddleLeft, C_TXT_HDR, "robotocondensed-bold.ttf");
            UILbl(cu, hdr, "0.010 0", "0.80 1", "             // ADMIN CONSOLE", 10, TextAnchor.MiddleLeft, C_TXT_DIM);
            UILbl(cu, hdr, "0.55 0", "0.88 1", $"{BasePlayer.activePlayerList.Count} online", 10, TextAnchor.MiddleRight, C_TXT_SEC);
            cu.Add(new CuiButton { Button = { Color = C_RED, Command = "prison.closeui" }, RectTransform = { AnchorMin = "0.960 0.10", AnchorMax = "0.998 0.90" }, Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = C_TXT_HDR, Font = "robotocondensed-bold.ttf" } }, hdr);

            // Stats bar
            string stats = panel + "_st";
            cu.Add(new CuiPanel { Image = { Color = C_BG0 }, RectTransform = { AnchorMin = "0 0.882", AnchorMax = "1 0.941" } }, panel, stats);
            UIStatBlock(cu, stats, "0.000", "0.200", jailedUIDs.Count.ToString(), "JAILED",      C_RED);
            UIStatBlock(cu, stats, "0.200", "0.400", yardActive.Count.ToString(), "IN YARD",     C_YELLOW);
            UIStatBlock(cu, stats, "0.400", "0.600", pendingJails.Count.ToString(),"PENDING",    C_ORANGE);
            UIStatBlock(cu, stats, "0.600", "0.800", cellLocations.Count.ToString(),"CELLS",     "0.55 0.55 0.55 1");
            UIStatBlock(cu, stats, "0.800", "1.000", freeTotal.ToString(),          "FREE",       C_GREEN);

            // Body layout
            string content = panel + "_cnt";
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0.002 0.03", AnchorMax = "0.652 0.878" } }, panel, content);

            // prisoners section
            string jSec = content + "_j";
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0.500", AnchorMax = "1 1" } }, content, jSec);

            // Section header
            string jSH = jSec + "_sh";
            cu.Add(new CuiPanel { Image = { Color = C_RED_HDR }, RectTransform = { AnchorMin = "0.01 0.940", AnchorMax = "0.99 1" } }, jSec, jSH);
            cu.Add(new CuiPanel { Image = { Color = C_RED_EDGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.006 1" } }, jSH);
            UILbl(cu, jSH, "0.02 0", "0.40 1", $"Prisoners", 11, TextAnchor.MiddleLeft, "0.85 0.55 0.55 1", "robotocondensed-bold.ttf");
            UILbl(cu, jSH, "0.40 0", "0.58 1", jailTotal.ToString(), 11, TextAnchor.MiddleLeft, C_TXT_DIM);
            if (jailPages > 1) UILbl(cu, jSH, "0.58 0", "0.72 1", $"pg {jailPage+1}/{jailPages}", 9, TextAnchor.MiddleCenter, C_TXT_DIM);
            bool jPrev = jailPage > 0, jNext = jailPage < jailPages - 1;
            cu.Add(new CuiButton { Button = { Color = jPrev ? C_RED_HDR : C_BTN_GRAY, Command = jPrev ? $"prison.page {jailPage-1} {freePage}" : "" }, RectTransform = { AnchorMin = "0.855 0.10", AnchorMax = "0.924 0.90" }, Text = { Text = "◀", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = jPrev ? "0.85 0.55 0.55 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, jSH);
            cu.Add(new CuiButton { Button = { Color = jNext ? C_RED_HDR : C_BTN_GRAY, Command = jNext ? $"prison.page {jailPage+1} {freePage}" : "" }, RectTransform = { AnchorMin = "0.930 0.10", AnchorMax = "0.999 0.90" }, Text = { Text = "▶", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = jNext ? "0.85 0.55 0.55 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, jSH);

            // Column headers
            string jHdr = jSec + "_hdr";
            cu.Add(new CuiPanel { Image = { Color = C_BG3 }, RectTransform = { AnchorMin = "0.01 0.884", AnchorMax = "0.99 0.937" } }, jSec, jHdr);
            UILbl(cu, jHdr, "0.030 0", "0.310 1", "PLAYER / STEAM ID", 11, TextAnchor.MiddleLeft,   C_TXT_SEC);
            UILbl(cu, jHdr, "0.310 0", "0.470 1", "REASON",            11, TextAnchor.MiddleLeft,   C_TXT_SEC);
            UILbl(cu, jHdr, "0.470 0", "0.565 1", "TIME",              11, TextAnchor.MiddleCenter, C_TXT_SEC);
            UILbl(cu, jHdr, "0.565 0", "0.635 1", "HP",                11, TextAnchor.MiddleCenter, C_TXT_SEC);
            UILbl(cu, jHdr, "0.635 0", "0.770 1", "STATUS",            11, TextAnchor.MiddleCenter, C_TXT_SEC);
            UILbl(cu, jHdr, "0.770 0", "1.000 1", "ACTIONS",           11, TextAnchor.MiddleCenter, C_TXT_SEC);

            // Prisoner rows
            float jRowH = 0.130f; float jTop = 0.880f;
            for (int i = 0; i < jailedPage.Count; i++)
            {
                ulong  uid   = jailedPage[i];
                bool   onl   = BasePlayer.FindByID(uid)?.IsConnected ?? false;
                bool   inYd  = yardActive.Contains(uid);
                string nm    = GetDisplayName(uid);
                string sid   = uid.ToString();
                string rsn   = jailReasons.TryGetValue(uid, out string jr2) ? jr2 : "—";
                string dur   = FormatTimeCompact(uid);
                float  hp2   = onl ? (BasePlayer.FindByID(uid)?.health ?? 0f) : -1f;

                Vector3 cv   = playerCells.TryGetValue(uid, out Vector3 cpv) ? cpv : Vector3.zero;
                int cIdx     = cv != Vector3.zero && cellLocations.Count > 0 ? cellLocations.IndexOf(cv) + 1 : 0;

                float yMax = jTop - i * jRowH;
                float yMin = yMax - jRowH + 0.006f;
                string rk  = jSec + "_r" + i;

                cu.Add(new CuiPanel { Image = { Color = i % 2 == 0 ? C_BG1 : C_BG2 }, RectTransform = { AnchorMin = $"0.01 {yMin:F4}", AnchorMax = $"0.99 {yMax:F4}" } }, jSec, rk);

                // Left accent bar
                string accentCol = inYd ? C_ACCENT_YARD : onl ? C_ACCENT_ONLINE : C_ACCENT_OFFLINE;
                cu.Add(new CuiPanel { Image = { Color = accentCol }, RectTransform = { AnchorMin = "0 0.12", AnchorMax = "0.007 0.88" } }, rk);

                // Name + SteamID — clickable to player info
                string sNm = nm.Length > 14 ? nm.Substring(0, 12) + ".." : nm;
                UILbl(cu, rk, "0.030 0.50", "0.310 1.00", sNm, 13, TextAnchor.MiddleLeft, C_TXT_PRI);
                cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = $"prison.player_ui {uid}" }, RectTransform = { AnchorMin = "0.030 0.00", AnchorMax = "0.310 0.52" }, Text = { Text = sid, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = C_BLUE, Font = "robotocondensed-regular.ttf" } }, rk);

                // Reason
                string sRsn = rsn.Length > 12 ? rsn.Substring(0, 10) + ".." : rsn;
                UILbl(cu, rk, "0.310 0", "0.470 1", sRsn, 12, TextAnchor.MiddleLeft, C_TXT_SEC);

                // Duration
                UILbl(cu, rk, "0.470 0", "0.565 1", dur, 12, TextAnchor.MiddleCenter, C_TXT_SEC);

                // HP
                string hpTxt = hp2 >= 0 ? $"{(int)hp2}" : "—";
                string hpCol = hp2 > 60 ? C_GREEN : hp2 > 30 ? C_YELLOW : hp2 >= 0 ? C_RED : C_TXT_DIM;
                UILbl(cu, rk, "0.565 0", "0.635 1", hpTxt, 12, TextAnchor.MiddleCenter, hpCol);

                // Status badge
                string bdgTxt = inYd ? "YARD" : onl ? (cIdx > 0 ? $"C{cIdx}" : "ON") : "OFF";
                string bdgBg  = inYd ? C_ACCENT_YARD : onl ? C_ACCENT_ONLINE : C_ACCENT_OFFLINE;
                string bk     = rk + "_b";
                cu.Add(new CuiPanel { Image = { Color = bdgBg }, RectTransform = { AnchorMin = "0.638 0.22", AnchorMax = "0.768 0.78" } }, rk, bk);
                UILbl(cu, bk, "0 0", "1 1", bdgTxt, 10, TextAnchor.MiddleCenter, C_TXT_HDR, "robotocondensed-bold.ttf");

                // Action buttons
                bool canYd = onl && !inYd;
                bool pbOk  = Payback3 != null;
                cu.Add(new CuiButton { Button = { Color = canYd ? C_BTN_YARD : C_BTN_GRAY, Command = canYd ? $"prison.yard_ui {uid}" : "" }, RectTransform = { AnchorMin = "0.774 0.16", AnchorMax = "0.856 0.84" }, Text = { Text = "YARD", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = canYd ? "0.75 1 0.75 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, rk);
                cu.Add(new CuiButton { Button = { Color = C_BTN_FREE, Command = $"prison.unjail_ui {uid}" }, RectTransform = { AnchorMin = "0.861 0.16", AnchorMax = "0.928 0.84" }, Text = { Text = "FREE", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 0.72 0.72 1", Font = "robotocondensed-bold.ttf" } }, rk);
                cu.Add(new CuiButton { Button = { Color = pbOk ? C_BTN_ABUSE : C_BTN_GRAY, Command = pbOk ? $"prison.payback_ui {uid} 0" : "" }, RectTransform = { AnchorMin = "0.933 0.16", AnchorMax = "0.995 0.84" }, Text = { Text = "⚡", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = pbOk ? "1 0.75 0.35 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, rk);
            }
            if (jailedPage.Count == 0)
                UILbl(cu, jSec, "0 0.20", "1 0.85", "No one's jailed right now.", 12, TextAnchor.MiddleCenter, C_TXT_DIM);

            // Section divider
            cu.Add(new CuiPanel { Image = { Color = C_DIVID }, RectTransform = { AnchorMin = "0.01 0.496", AnchorMax = "0.99 0.502" } }, content);

            // free players section
            string fSec = content + "_f";
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.494" } }, content, fSec);

            // Section header
            string fSH = fSec + "_sh";
            cu.Add(new CuiPanel { Image = { Color = C_GREEN_HDR }, RectTransform = { AnchorMin = "0.01 0.915", AnchorMax = "0.99 1" } }, fSec, fSH);
            cu.Add(new CuiPanel { Image = { Color = C_GREEN_EDGE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.006 1" } }, fSH);
            UILbl(cu, fSH, "0.02 0", "0.40 1", $"Online — Free", 11, TextAnchor.MiddleLeft, "0.60 0.88 0.60 1", "robotocondensed-bold.ttf");
            UILbl(cu, fSH, "0.40 0", "0.58 1", freeTotal.ToString(), 11, TextAnchor.MiddleLeft, C_TXT_DIM);
            if (freePages > 1) UILbl(cu, fSH, "0.58 0", "0.72 1", $"pg {freePage+1}/{freePages}", 9, TextAnchor.MiddleCenter, C_TXT_DIM);
            bool fPrev = freePage > 0, fNext = freePage < freePages - 1;
            cu.Add(new CuiButton { Button = { Color = fPrev ? C_GREEN_HDR : C_BTN_GRAY, Command = fPrev ? $"prison.page {jailPage} {freePage-1}" : "" }, RectTransform = { AnchorMin = "0.855 0.10", AnchorMax = "0.924 0.90" }, Text = { Text = "◀", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = fPrev ? "0.60 0.88 0.60 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, fSH);
            cu.Add(new CuiButton { Button = { Color = fNext ? C_GREEN_HDR : C_BTN_GRAY, Command = fNext ? $"prison.page {jailPage} {freePage+1}" : "" }, RectTransform = { AnchorMin = "0.930 0.10", AnchorMax = "0.999 0.90" }, Text = { Text = "▶", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = fNext ? "0.60 0.88 0.60 1" : C_TXT_DIM, Font = "robotocondensed-bold.ttf" } }, fSH);

            // Column headers
            string fHdr2 = fSec + "_hdr";
            cu.Add(new CuiPanel { Image = { Color = C_BG3 }, RectTransform = { AnchorMin = "0.01 0.856", AnchorMax = "0.99 0.910" } }, fSec, fHdr2);
            UILbl(cu, fHdr2, "0.030 0", "0.600 1", "PLAYER / STEAM ID", 11, TextAnchor.MiddleLeft,   C_TXT_SEC);
            UILbl(cu, fHdr2, "0.600 0", "0.720 1", "PING",              11, TextAnchor.MiddleCenter, C_TXT_SEC);
            UILbl(cu, fHdr2, "0.720 0", "0.810 1", "HP",                11, TextAnchor.MiddleCenter, C_TXT_SEC);
            UILbl(cu, fHdr2, "0.810 0", "1.000 1", "ACTION",            11, TextAnchor.MiddleCenter, C_TXT_SEC);

            // Free player rows
            float fRowH = 0.128f; float fTop = 0.852f;
            for (int i = 0; i < freePage_.Count; i++)
            {
                BasePlayer fp   = freePage_[i];
                string     fNm  = fp.displayName.Length > 18 ? fp.displayName.Substring(0, 16) + ".." : fp.displayName;
                int        ping = Network.Net.sv.GetAveragePing(fp.net.connection);
                float      fhp  = fp.health;
                float fyMax = fTop - i * fRowH;
                float fyMin = fyMax - fRowH + 0.006f;
                string frk  = fSec + "_r" + i;

                cu.Add(new CuiPanel { Image = { Color = i % 2 == 0 ? C_BG1 : C_BG2 }, RectTransform = { AnchorMin = $"0.01 {fyMin:F4}", AnchorMax = $"0.99 {fyMax:F4}" } }, fSec, frk);
                cu.Add(new CuiPanel { Image = { Color = C_ACCENT_FREE }, RectTransform = { AnchorMin = "0 0.12", AnchorMax = "0.007 0.88" } }, frk);

                // Name (top) + SteamID (bottom, clickable)
                UILbl(cu, frk, "0.030 0.50", "0.600 1.00", fNm, 13, TextAnchor.MiddleLeft, C_TXT_PRI);
                cu.Add(new CuiButton { Button = { Color = "0 0 0 0", Command = $"prison.player_ui {fp.userID}" }, RectTransform = { AnchorMin = "0.030 0.00", AnchorMax = "0.600 0.52" }, Text = { Text = fp.UserIDString, FontSize = 11, Align = TextAnchor.MiddleLeft, Color = C_BLUE, Font = "robotocondensed-regular.ttf" } }, frk);

                // Ping
                string pCol = ping < 80 ? C_GREEN : ping < 150 ? C_YELLOW : C_RED;
                UILbl(cu, frk, "0.600 0", "0.720 1", $"{ping}ms", 12, TextAnchor.MiddleCenter, pCol);

                // HP
                string hpCol2 = fhp > 60 ? C_GREEN : fhp > 30 ? C_YELLOW : C_RED;
                UILbl(cu, frk, "0.720 0", "0.810 1", $"{(int)fhp}", 12, TextAnchor.MiddleCenter, hpCol2);

                // Jail button
                cu.Add(new CuiButton { Button = { Color = C_BTN_JAIL, Command = $"prison.reason_ui {fp.userID}" }, RectTransform = { AnchorMin = "0.820 0.16", AnchorMax = "0.994 0.84" }, Text = { Text = "JAIL", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 0.72 0.72 1", Font = "robotocondensed-bold.ttf" } }, frk);
            }
            if (freePage_.Count == 0)
                UILbl(cu, fSec, "0 0.20", "1 0.80", "No other players online.", 11, TextAnchor.MiddleCenter, C_TXT_DIM);

            // right sidebar
            string right = panel + "_right";
            cu.Add(new CuiPanel { Image = { Color = "0.060 0.060 0.060 1" }, RectTransform = { AnchorMin = "0.655 0.03", AnchorMax = "0.997 0.878" } }, panel, right);

            // Quick actions
            UILbl(cu, right, "0.05 0.930", "0.95 0.965", "QUICK ACTIONS", 11, TextAnchor.MiddleLeft, C_TXT_SEC);
            UIActBtn(cu, right, "0.04 0.858", "0.96 0.926", "Yard all prisoners", "prison.yardall_ui",              C_BTN_YARD);
            UIActBtn(cu, right, "0.04 0.786", "0.96 0.854", "Refresh",            $"prison.refresh {jailPage} {freePage}", C_BTN_BLUE);
            UIActBtn(cu, right, "0.04 0.714", "0.96 0.782", "Close  [ESC]",       "prison.closeui",                 C_BG3);

            // Cells
            UILbl(cu, right, "0.05 0.672", "0.95 0.706", "CELLS", 11, TextAnchor.MiddleLeft, C_TXT_SEC);
            int   maxCells = Mathf.Min(cellLocations.Count, 4);
            float cellH    = 0.060f;
            float cellTop  = 0.668f;
            for (int i = 0; i < maxCells; i++)
            {
                float  cy2  = cellTop - i * cellH;
                float  cy1  = cy2 - cellH + 0.004f;
                string ck   = right + "_c" + i;
                int    occ  = playerCells.Values.Count(v => v == cellLocations[i]);
                Vector3 cv3 = cellLocations[i];
                cu.Add(new CuiPanel { Image = { Color = C_BG2 }, RectTransform = { AnchorMin = $"0.04 {cy1:F4}", AnchorMax = $"0.96 {cy2:F4}" } }, right, ck);
                cu.Add(new CuiPanel { Image = { Color = occ > 0 ? C_RED : C_ACCENT_OFFLINE }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.014 1" } }, ck);
                UILbl(cu, ck, "0.06 0", "0.22 1", $"#{i+1}", 11, TextAnchor.MiddleLeft,  C_TXT_SEC);
                UILbl(cu, ck, "0.22 0", "0.76 1", $"{cv3.x:F0},{cv3.y:F0},{cv3.z:F0}", 11, TextAnchor.MiddleLeft, C_TXT_SEC);
                UILbl(cu, ck, "0.76 0", "0.98 1", $"{occ}P", 11, TextAnchor.MiddleRight, occ > 0 ? C_RED : C_TXT_DIM);
            }
            if (cellLocations.Count == 0)
                UILbl(cu, right, "0.04 0.44", "0.96 0.66", "No cells defined.\nUse /addcell.", 10, TextAnchor.MiddleCenter, C_TXT_DIM);

            // Event log
            float cellBot  = cellTop - maxCells * cellH - 0.024f;
            float logFloor = 0.10f;
            float logH     = 0.054f;
            UILbl(cu, right, $"0.05 {cellBot - 0.034f:F4}", $"0.95 {cellBot:F4}", "RECENT EVENTS", 11, TextAnchor.MiddleLeft, C_TXT_SEC);
            float lTop = cellBot - 0.036f;
            int logShow = Mathf.Min(_eventLog.Count, Mathf.Max(0, (int)((lTop - logFloor) / logH)));
            for (int i = 0; i < logShow; i++)
            {
                LogEntry e   = _eventLog[i];
                float    ly2 = lTop - i * logH;
                float    ly1 = ly2 - logH + 0.003f;
                if (ly1 < logFloor) break;
                string lk = right + "_l" + i;
                cu.Add(new CuiPanel { Image = { Color = C_BG2 }, RectTransform = { AnchorMin = $"0.04 {ly1:F4}", AnchorMax = $"0.96 {ly2:F4}" } }, right, lk);
                cu.Add(new CuiPanel { Image = { Color = e.Color }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0.016 1" } }, lk);
                UILbl(cu, lk, "0.04 0.48", "0.24 1.00", e.Time, 11, TextAnchor.MiddleLeft, C_TXT_SEC);
                UILbl(cu, lk, "0.24 0.48", "0.99 1.00", e.Text, 11, TextAnchor.MiddleLeft, e.Color);
            }

            // Pending offline banner
            if (pendingJails.Count > 0)
                cu.Add(new CuiButton
                {
                    Button        = { Color = "0.18 0.10 0.01 1", Command = "prison.pending_ui" },
                    RectTransform = { AnchorMin = "0.04 0.01", AnchorMax = "0.96 0.09" },
                    Text          = { Text = $"▶  {pendingJails.Count} pending offline jail(s) — click to manage", FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 0.78 0.35 1", Font = "robotocondensed-bold.ttf" }
                }, right);

            CuiHelper.AddUi(admin, cu);
        }

        // ui helpers

        private void UIStatBlock(CuiElementContainer cu, string parent, string xMin, string xMax, string val, string label, string valColor)
        {
            string k = parent + "_sb" + label;
            cu.Add(new CuiPanel { Image = { Color = "0 0 0 0" }, RectTransform = { AnchorMin = $"{xMin} 0", AnchorMax = $"{xMax} 1" } }, parent, k);
            if (xMax != "1.000") cu.Add(new CuiPanel { Image = { Color = C_DIVID }, RectTransform = { AnchorMin = "0.985 0.20", AnchorMax = "1.000 0.80" } }, k);
            cu.Add(new CuiLabel { Text = { Text = val,   FontSize = 18, Align = TextAnchor.MiddleCenter, Color = valColor,   Font = "robotocondensed-bold.ttf"    }, RectTransform = { AnchorMin = "0 0.36", AnchorMax = "1 1"    } }, k);
            cu.Add(new CuiLabel { Text = { Text = label, FontSize = 7,  Align = TextAnchor.MiddleCenter, Color = C_TXT_DIM,  Font = "robotocondensed-regular.ttf" }, RectTransform = { AnchorMin = "0 0",    AnchorMax = "1 0.42" } }, k);
        }

        private void UIActBtn(CuiElementContainer cu, string parent, string aMin, string aMax, string label, string cmd, string color)
        {
            cu.Add(new CuiButton { Button = { Color = color, Command = cmd }, RectTransform = { AnchorMin = aMin, AnchorMax = aMax }, Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = C_TXT_PRI, Font = "robotocondensed-bold.ttf" } }, parent);
        }

        private void UILbl(CuiElementContainer cu, string parent, string aMin, string aMax, string text, int size, TextAnchor align, string color, string font = "robotocondensed-regular.ttf")
        {
            cu.Add(new CuiLabel { Text = { Text = text, FontSize = size, Align = align, Color = color, Font = font }, RectTransform = { AnchorMin = aMin, AnchorMax = aMax } }, parent);
        }

        // helpers

        private string FormatDuration(ulong uid)
        {
            if (!TryGetSentenceInfo(uid, out long elapsed, out _, out _)) return "—";
            return FormatSeconds(elapsed);
        }

        private string GetDisplayName(ulong uid) => originalNames.TryGetValue(uid, out string n) ? n : uid.ToString();

        private bool IsInRace(BasePlayer player) { if (player == null || RaceTrack == null) return false; try { return RaceTrack.Call("IsEventPlayer", player) != null; } catch { return false; } }

        private void TryLeaveRace(BasePlayer player) { if (player == null || RaceTrack == null) return; try { RaceTrack.Call("cmdRace", player, "race", new string[] { "leave" }); } catch (System.Exception ex) { Puts($"[Prison] TryLeaveRace error: {ex.Message}"); } }

        // corelobby / gamemode integration (release-time)

        // add the class name here (not the [Info] title) if another gamemode shows up
        private static readonly string[] GamemodePluginNames = { "PublicDuels", "PrimitiveFFA", "AKFFA" };

        // reflection, not hook dispatch - and cast userID to ulong first, Carbon wraps it
        private bool RemoveFromGamemodes(BasePlayer player)
        {
            if (player == null) return false;
            bool removedFromAny = false;
            foreach (string name in GamemodePluginNames)
            {
                Plugin gm = plugins.Find(name);
                if (gm == null) continue;
                try
                {
                    var method = gm.GetType().GetMethod("ForceLeave");
                    if (method == null) continue;
                    object result = method.Invoke(gm, new object[] { (ulong)player.userID });
                    if (result is bool wasIn && wasIn) removedFromAny = true;
                }
                catch (System.Exception ex)
                {
                    Puts($"[Prison] RemoveFromGamemodes({name}) error: {ex.Message}");
                }
            }
            return removedFromAny;
        }

        // false if CoreLobby isn't loaded or has no spawns yet - caller falls back
        private bool TryGetCoreLobbySpawn(out Vector3 pos)
        {
            pos = default(Vector3);
            Plugin core = plugins.Find("CoreLobby");
            if (core == null) return false;
            try
            {
                var hasMethod = core.GetType().GetMethod("HasSpawnPoints");
                if (hasMethod == null || !(hasMethod.Invoke(core, null) is bool has) || !has) return false;

                var getMethod = core.GetType().GetMethod("GetNextLobbySpawn");
                if (getMethod == null) return false;

                if (!(getMethod.Invoke(core, null) is Vector3 v)) return false;
                pos = v;
                return true;
            }
            catch (System.Exception ex)
            {
                PrintWarning($"[Prison] CoreLobby reflection call failed, falling back: {ex}");
                return false;
            }
        }

        // debug cmd for the reflection calls above, defaults to yourself
        [ChatCommand("gmdebug")]
        private void GmDebugCommand(BasePlayer admin, string command, string[] args)
        {
            if (!HasPermission(admin, PermUse)) return;
            BasePlayer target = args.Length >= 1 ? FindPlayer(args[0]) : admin;
            if (target == null) { admin.ChatMessage("Player not found."); return; }

            admin.ChatMessage($"<color=#ffaa00>=== Gamemode integration check for {target.displayName} ===</color>");

            Plugin core = plugins.Find("CoreLobby");
            admin.ChatMessage(core == null
                ? "<color=#ff5555>CoreLobby: NOT FOUND - plugins.Find(\"CoreLobby\") returned null. Is it loaded?</color>"
                : "<color=#00ff00>CoreLobby: found.</color>");

            foreach (string name in GamemodePluginNames)
            {
                Plugin gm = plugins.Find(name);
                if (gm == null)
                {
                    admin.ChatMessage($"<color=#ff5555>{name}: NOT FOUND - plugins.Find(\"{name}\") returned null. Is it loaded under exactly this class name?</color>");
                    continue;
                }

                var method = gm.GetType().GetMethod("ForceLeave");
                if (method == null)
                {
                    admin.ChatMessage($"<color=#ff5555>{name}: loaded, but has NO ForceLeave(ulong) method - you're running an OLD copy of {name}.cs. Redeploy the updated file and reload it.</color>");
                    continue;
                }

                try
                {
                    object result = method.Invoke(gm, new object[] { (ulong)target.userID });
                    bool wasIn = result is bool b && b;
                    admin.ChatMessage(wasIn
                        ? $"<color=#00ff00>{name}: found, player WAS in this gamemode - ForceLeave ran and removed them.</color>"
                        : $"<color=#999999>{name}: found, but player was not in this gamemode (no-op).</color>");
                }
                catch (System.Exception ex)
                {
                    admin.ChatMessage($"<color=#ff5555>{name}: ForceLeave threw: {ex.InnerException?.Message ?? ex.Message}</color>");
                }
            }
        }

        private void HardDismount(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            player.EnsureDismounted(); player.SetParent(null, true, true);
            BaseMountable mount = player.GetMounted(); if (mount != null) mount.DismountPlayer(player, true);
            ModularCar car = player.GetMountedVehicle() as ModularCar;
            if (car != null) foreach (var module in car.AttachedModuleEntities) foreach (var seat in module.GetComponentsInChildren<BaseMountable>()) if (seat.GetMounted() == player) { seat.DismountPlayer(player, true); break; }
            BaseVehicle vehicle = player.GetMountedVehicle();
            if (vehicle != null) { BaseMountable ds = vehicle.GetComponent<BaseMountable>(); if (ds != null) ds.DismountAllPlayers(); else vehicle.DismountAllPlayers(); }
        }

        private void ForceReturnToPrison(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !IsJailed(player)) return;
            if (teleporting.Contains(player.userID)) return;
            teleporting.Add(player.userID); HardDismount(player);
            NextTick(() => { if (player == null || !player.IsConnected) { teleporting.Remove(player?.userID ?? 0); return; } player.SetParent(null, true, true); player.Teleport(GetPlayerCell(player)); teleporting.Remove(player.userID); });
        }

        private void ForceReturnToYard(BasePlayer player)
        {
            if (player == null || !player.IsConnected || !IsJailed(player)) return;
            if (teleporting.Contains(player.userID)) return;
            teleporting.Add(player.userID); player.ChatMessage("<color=#ff0000>Stay in the yard, shitter.</color>");
            NextTick(() => { if (player == null || !player.IsConnected) { teleporting.Remove(player?.userID ?? 0); return; } player.SetParent(null, true, true); player.Teleport(yardLocation); teleporting.Remove(player.userID); });
        }

        private void RenamePlayer(BasePlayer player)
        {
            if (player == null) return;
            if (!originalNames.ContainsKey(player.userID)) { originalNames[player.userID] = player.displayName; Interface.Oxide.DataFileSystem.WriteObject(NamesDataFile, originalNames); }
            player.displayName = "Shitter69"; player.IPlayer.Name = "Shitter69";
            if (player.net?.connection != null) player.net.connection.username = "Shitter69";
            player.SendNetworkUpdateImmediate();
        }

        private void RestoreName(BasePlayer player)
        {
            if (player == null || !originalNames.TryGetValue(player.userID, out string name)) return;
            player.displayName = name; player.IPlayer.Name = name;
            if (player.net?.connection != null) player.net.connection.username = name;
            player.SendNetworkUpdateImmediate();
            originalNames.Remove(player.userID);
            Interface.Oxide.DataFileSystem.WriteObject(NamesDataFile, originalNames);
        }

        private void EquipPrisonUniform(BasePlayer player)
        {
            player.inventory.Strip();
            GiveItem(player, PrisonHoodie, HoodieSkin, player.inventory.containerWear);
            GiveItem(player, PrisonPants,  PantsSkin,  player.inventory.containerWear);
            player.SendNetworkUpdateImmediate();
        }

        private void StripYardPickaxe(BasePlayer player)
        {
            foreach (Item item in player.inventory.containerBelt.itemList.ToList()) if (item.info.shortname == YardPickaxe && item.skin == YardPickaxeSkin) item.Remove();
            foreach (Item item in player.inventory.containerMain.itemList.ToList()) if (item.info.shortname == YardPickaxe && item.skin == YardPickaxeSkin) item.Remove();
        }

        private void GiveItem(BasePlayer player, string shortname, ulong skin, ItemContainer container)
        { Item item = ItemManager.CreateByName(shortname, 1, skin); item?.MoveToContainer(container); }

        private void SaveInventory(BasePlayer player)
        {
            if (savedInventories.ContainsKey(player.userID)) return;
            var list = new List<SavedItem>();
            CaptureContainer(player.inventory.containerWear, SlotType.Wear, list);
            CaptureContainer(player.inventory.containerMain, SlotType.Main, list);
            CaptureContainer(player.inventory.containerBelt, SlotType.Belt, list);
            savedInventories[player.userID] = list;
            Interface.Oxide.DataFileSystem.WriteObject(InvDataFile, savedInventories);
        }

        private void CaptureContainer(ItemContainer container, SlotType slot, List<SavedItem> list)
        { foreach (Item item in container.itemList) list.Add(new SavedItem { shortname = item.info.shortname, amount = item.amount, skin = item.skin, slot = slot }); }

        private void RestoreInventory(BasePlayer player)
        {
            if (!savedInventories.TryGetValue(player.userID, out var items)) return;
            player.inventory.Strip();
            foreach (SavedItem data in items)
            {
                Item item = ItemManager.CreateByName(data.shortname, data.amount, data.skin); if (item == null) continue;
                ItemContainer target = data.slot == SlotType.Wear ? player.inventory.containerWear : data.slot == SlotType.Belt ? player.inventory.containerBelt : player.inventory.containerMain;
                item.MoveToContainer(target);
            }
            savedInventories.Remove(player.userID);
            Interface.Oxide.DataFileSystem.WriteObject(InvDataFile, savedInventories);
        }

        private bool IsJailed(BasePlayer player) => jailedPlayers.ContainsKey(player.userID);

        private bool HasPermission(BasePlayer player, string perm)
        { if (permission.UserHasPermission(player.UserIDString, perm)) return true; player.ChatMessage("You don't have permission to use this command."); return false; }

        private BasePlayer FindPlayer(string arg)
        {
            if (ulong.TryParse(arg, out ulong id)) return BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
            string lower = arg.ToLower();
            return BasePlayer.activePlayerList.Concat(BasePlayer.sleepingPlayerList).FirstOrDefault(p => p.displayName.ToLower().Contains(lower) || (originalNames.TryGetValue(p.userID, out string orig) && orig.ToLower().Contains(lower)));
        }

        // config

        protected override void LoadDefaultConfig()
        {
            Config["Prison"] = new Dictionary<string, object> { ["X"] = 0f, ["Y"] = 20f, ["Z"] = 0f, ["Radius"] = 6f };
            Config["Yard"]   = new Dictionary<string, object> { ["X"] = 0f, ["Y"] = 20f, ["Z"] = 0f };
            // sentence length per jail reason, in MINUTES. 0 = indefinite (only /unjail lets them out).
            // "Default" is used for any reason that doesn't match one of these exactly (e.g. free-typed
            // reasons from /prison <player> <reason>).
            Config["Sentences", "Toxicity"]     = 20f;
            Config["Sentences", "Exploiting"]   = 180f;
            Config["Sentences", "Racism"]       = 5f;
            Config["Sentences", "Admin Jailed"] = 0f;
            Config["Sentences", "5Head Abuse"]  = 0f;
            Config["Sentences", "Default"]      = 0f;

            // escalation ladders: minutes per offense (index 0 = 1st time jailed for this reason,
            // index 1 = 2nd time, ...). Once past the end of the list, the LAST stage repeats —
            // so a last stage of 0 means "permanent from then on". A reason with no ladder here
            // just uses its flat Sentences value every time, no escalation.
            Config["Escalation", "Racism"]     = new List<object> { 5f, 10f, 15f, 30f, 60f, 120f }; // progressive chances; max 2h
            Config["Escalation", "Toxicity"]   = new List<object> { 5f, 15f, 45f, 120f, 0f };  // 5m -> 15m -> 45m -> 2h -> permanent from the 5th
            Config["Escalation", "Exploiting"] = new List<object> { 30f, 120f, 360f, 0f };     // 30m -> 2h -> 6h -> permanent from the 4th
        }

        // adds any missing "Sentences" keys to an existing config on disk, so upgrading the
        // plugin doesn't require deleting the config file to pick up the new feature
        private void EnsureSentenceDefaults()
        {
            var defaults = new Dictionary<string, float>
            {
                ["Toxicity"]     = 20f,
                ["Exploiting"]   = 180f,
                ["Racism"]       = 5f,
                ["Admin Jailed"] = 0f,
                ["5Head Abuse"]  = 0f,
                ["Default"]      = 0f,
            };
            bool changed = false;
            foreach (var kvp in defaults)
            {
                if (Config["Sentences", kvp.Key] == null) { Config["Sentences", kvp.Key] = kvp.Value; changed = true; }
            }
            // Migrate just the original shipped racism ladder. Preserve any
            // administrator-customised ladder so server settings are respected.
            object currentRacismLadder = Config["Escalation", "Racism"];
            if (currentRacismLadder == null || IsLegacyRacismLadder(currentRacismLadder))
            {
                Config["Escalation", "Racism"] = new List<object> { 5f, 10f, 15f, 30f, 60f, 120f };
                changed = true;
            }
            // A legacy flat fallback of 120 minutes is likewise shortened.
            // Custom sentence fallbacks with other values are not changed.
            object currentRacismSentence = Config["Sentences", "Racism"];
            if (currentRacismSentence != null &&
                float.TryParse(currentRacismSentence.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float oldRacismMinutes) &&
                oldRacismMinutes == 120f)
            {
                Config["Sentences", "Racism"] = 5f;
                changed = true;
            }
            if (Config["Escalation", "Toxicity"] == null)
            {
                Config["Escalation", "Toxicity"] = new List<object> { 5f, 15f, 45f, 120f, 0f };
                changed = true;
            }
            if (Config["Escalation", "Exploiting"] == null)
            {
                Config["Escalation", "Exploiting"] = new List<object> { 30f, 120f, 360f, 0f };
                changed = true;
            }
            if (changed) SaveConfig();
        }

        private bool IsLegacyRacismLadder(object configured)
        {
            // Handles both normal .NET lists and Newtonsoft JSON arrays read from
            // an existing Oxide config without resetting offence counts.
            if (configured == null || configured is string) return false;
            if (!(configured is System.Collections.IEnumerable values)) return false;
            var oldStages = new List<float>();
            foreach (object raw in values)
            {
                if (raw == null ||
                    !float.TryParse(raw.ToString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float minutes)) return false;
                oldStages.Add(minutes);
                if (oldStages.Count > 4) return false;
            }
            return oldStages.Count == 4 &&
                oldStages[0] == 5f && oldStages[1] == 20f &&
                oldStages[2] == 240f && oldStages[3] == 0f;
        }

        private void LoadConfigValues()
        {
            EnsureSentenceDefaults();
            prisonLocation    = new Vector3(GetConfigFloat("Prison", "X"), GetConfigFloat("Prison", "Y"), GetConfigFloat("Prison", "Z"));
            prisonRadius      = GetConfigFloat("Prison", "Radius");
            yardLocation      = new Vector3(GetConfigFloat("Yard", "X"), GetConfigFloat("Yard", "Y"), GetConfigFloat("Yard", "Z"));
        }

        private float GetConfigFloat(string category, string key) => Config[category, key] == null ? 0f : System.Convert.ToSingle(Config[category, key]);

        // sentences

        // looks up the configured sentence length (minutes) for a reason, exact match first,
        // then case-insensitive, then falls back to Sentences.Default. Returns seconds; 0 = indefinite.
        // reasons with a Sentences.Escalation ladder get progressively longer sentences per repeat
        // offense (tracked per player, per reason), capping at whatever the last stage says —
        // set that last stage to 0 to mean "goes permanent after N strikes"
        private long GetSentenceSeconds(ulong uid, string reason)
        {
            string canonicalKey = reason;
            object escVal = Config["Escalation", reason];
            if (escVal == null && Config["Escalation"] is Dictionary<string, object> escDict)
            {
                foreach (var kvp in escDict)
                {
                    if (string.Equals(kvp.Key, reason, System.StringComparison.OrdinalIgnoreCase))
                    { escVal = kvp.Value; canonicalKey = kvp.Key; break; }
                }
            }
            if (escVal is List<object> stages && stages.Count > 0)
            {
                int offenseIndex = GetOffenseCount(uid, canonicalKey); // 0 = first time with this reason
                IncrementOffenseCount(uid, canonicalKey);
                SaveOffenseCountsData();
                int idx = System.Math.Min(offenseIndex, stages.Count - 1);
                float stageMinutes = System.Convert.ToSingle(stages[idx]);
                return stageMinutes <= 0f ? 0L : (long)(stageMinutes * 60f);
            }

            // flat, non-escalating sentence (existing behaviour)
            object val = Config["Sentences", reason];
            if (val == null && Config["Sentences"] is Dictionary<string, object> dict)
            {
                foreach (var kvp in dict)
                {
                    if (string.Equals(kvp.Key, reason, System.StringComparison.OrdinalIgnoreCase))
                    { val = kvp.Value; break; }
                }
            }
            float minutes = val != null ? System.Convert.ToSingle(val) : GetConfigFloat("Sentences", "Default");
            return minutes <= 0f ? 0L : (long)(minutes * 60f);
        }

        private int GetOffenseCount(ulong uid, string reasonKey)
        {
            if (!jailOffenseCounts.TryGetValue(uid, out var byReason)) return 0;
            return byReason.TryGetValue(reasonKey, out int c) ? c : 0;
        }

        private void IncrementOffenseCount(ulong uid, string reasonKey)
        {
            if (!jailOffenseCounts.TryGetValue(uid, out var byReason))
            { byReason = new Dictionary<string, int>(); jailOffenseCounts[uid] = byReason; }
            byReason[reasonKey] = (byReason.TryGetValue(reasonKey, out int c) ? c : 0) + 1;
        }

        private long UnixNow() => (long)(System.DateTime.UtcNow - new System.DateTime(1970, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)).TotalSeconds;

        // elapsed/remaining, seconds. hasSentence is false for indefinite jails (remaining is meaningless then).
        private bool TryGetSentenceInfo(ulong uid, out long elapsed, out long remaining, out bool hasSentence)
        {
            elapsed = 0; remaining = 0; hasSentence = false;
            if (!jailStartTimes.TryGetValue(uid, out long start)) return false;
            elapsed = UnixNow() - start;
            if (elapsed < 0) elapsed = 0;
            if (jailDurations.TryGetValue(uid, out long dur) && dur > 0)
            {
                hasSentence = true;
                remaining = dur - elapsed;
                if (remaining < 0) remaining = 0;
            }
            return true;
        }

        private string FormatSeconds(long secs)
        {
            if (secs < 0) secs = 0;
            if (secs < 60)   return $"{secs}s";
            if (secs < 3600) return $"{secs / 60}m {secs % 60}s";
            return $"{secs / 3600}h {(secs % 3600) / 60}m";
        }

        // compact form for the narrow list column: "-18m" = 18m left on a timed sentence,
        // "+12m" = been in 12m with no set release (indefinite, counting up)
        private string FormatTimeCompact(ulong uid)
        {
            if (!TryGetSentenceInfo(uid, out long elapsed, out long remaining, out bool hasSentence)) return "—";
            return hasSentence ? $"-{FormatSeconds(remaining)}" : $"+{FormatSeconds(elapsed)}";
        }

        // fuller form for the player-info panel
        private string FormatTimeFull(ulong uid)
        {
            if (!TryGetSentenceInfo(uid, out long elapsed, out long remaining, out bool hasSentence)) return "—";
            return hasSentence ? $"{FormatSeconds(remaining)} left (served {FormatSeconds(elapsed)})" : $"{FormatSeconds(elapsed)} (indefinite)";
        }

        // player-facing phrasing (used in chat messages to the jailed player themselves)
        private string DescribeSentenceForPlayer(ulong uid)
        {
            if (jailDurations.TryGetValue(uid, out long dur) && dur > 0) return $"for {FormatSeconds(dur)}";
            return "indefinitely — an admin has to release you";
        }

        private string DescribeRemainingForPlayer(ulong uid)
        {
            if (!TryGetSentenceInfo(uid, out _, out long remaining, out bool hasSentence)) return "";
            return hasSentence ? $" You have {FormatSeconds(remaining)} left." : " You're in indefinitely — an admin has to release you.";
        }

        // ambient effects

        private void StartFakeReleaseTimer(BasePlayer player)
        {
            if (fakeTimerActive.Contains(player.userID)) return;
            fakeTimerActive.Add(player.userID);
            timer.Once(FakeTimerInterval, () => RunCountdown(player));
        }

        private void RunCountdown(BasePlayer player)
        {
            if (player == null || !IsJailed(player)) { fakeTimerActive.Remove(player?.userID ?? 0); return; }
            int countdown = 10;
            timer.Repeat(1f, 11, () =>
            {
                if (player == null || !player.IsConnected || !IsJailed(player)) { fakeTimerActive.Remove(player?.userID ?? 0); return; }
                if (countdown > 0) { player.ChatMessage($"<color=#00ff00>You will be released in {countdown}...</color>"); countdown--; }
                else { player.ChatMessage("<color=#ff0000>You wish you shitter.</color>"); timer.Once(FakeTimerInterval, () => RunCountdown(player)); }
            });
        }

        private void StartHeliSound(BasePlayer player) { if (heliActive.Contains(player.userID)) return; heliActive.Add(player.userID); ScheduleNextHeli(player); }

        private void ScheduleNextHeli(BasePlayer player)
        {
            if (player == null || !IsJailed(player)) { heliActive.Remove(player?.userID ?? 0); return; }
            timer.Once(UnityEngine.Random.Range(180f, 420f), () =>
            {
                if (player == null || !player.IsConnected || !IsJailed(player)) { heliActive.Remove(player?.userID ?? 0); return; }
                timer.Repeat(0.6f, 6, () => SendEffectToPlayer(player, GrenadeEffect));
                player.ChatMessage("<color=#ff8800>[SERVER]</color> A patrol helicopter is circling the area...");
                ScheduleNextHeli(player);
            });
        }

        private void StartDeathSound(BasePlayer player) { if (deathActive.Contains(player.userID)) return; deathActive.Add(player.userID); ScheduleNextDeath(player); }

        private void ScheduleNextDeath(BasePlayer player)
        {
            if (player == null || !IsJailed(player)) { deathActive.Remove(player?.userID ?? 0); return; }
            timer.Once(UnityEngine.Random.Range(240f, 480f), () =>
            {
                if (player == null || !player.IsConnected || !IsJailed(player)) { deathActive.Remove(player?.userID ?? 0); return; }
                SendEffectToPlayer(player, DeathEffect);
                ScheduleNextDeath(player);
            });
        }

        private void SendEffectToPlayer(BasePlayer player, string effectPath)
        {
            if (player?.net?.connection == null) return;
            var effect = new Effect();
            effect.Init(Effect.Type.Generic, player.transform.position, Vector3.zero);
            effect.pooledString = effectPath;
            EffectNetwork.Send(effect, player.net.connection);
        }

        // Public Rust chat format: 5Heads Arena branded single-event notice.
        // Strip player-supplied rich-text tags and newlines from public announcements.
        private string CleanAnnouncementText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            string cleaned = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]*>", "");
            cleaned = cleaned.Replace("\r", " ").Replace("\n", " ").Trim();
            return cleaned.Length > 180 ? cleaned.Substring(0, 180) : cleaned;
        }

        private string FormatAnnouncementDuration(long seconds)
        {
            if (seconds >= 86400 && seconds % 86400 == 0)
            {
                long days = seconds / 86400;
                return days + (days == 1 ? " day" : " days");
            }
            if (seconds >= 3600 && seconds % 3600 == 0)
            {
                long hours = seconds / 3600;
                return hours + (hours == 1 ? " hour" : " hours");
            }
            if (seconds >= 60)
            {
                long minutes = (seconds + 59) / 60;
                return minutes + (minutes == 1 ? " minute" : " minutes");
            }
            return seconds + (seconds == 1 ? " second" : " seconds");
        }

        // prison event delivery
        // This plugin is the source of truth for UI, chat, console and API jails.
        // Railway's Arena bot already holds separate owner/VIP Discord webhooks.
        // Emit one structured server-console event after JailPlayer succeeds;
        // its WebRCON listener posts two independent embeds with the right privacy.
        // No webhook secrets are stored in the Rust server plugin.
        private void ReportPrisonToArenaBot(BasePlayer player, string originalName, string reason)
        {
            try
            {
                string json = JsonConvert.SerializeObject(new
                {
                    steamId = player.UserIDString,
                    username = originalName,
                    reason = string.IsNullOrWhiteSpace(reason) ? "Not supplied by admin UI" : reason,
                    durationSeconds = jailDurations.TryGetValue(player.userID, out long seconds) ? seconds : 0L
                });
                Puts("[5HEADS_PRISON] " + json);
            }
            catch (System.Exception ex)
            {
                PrintError("[Prison] Failed to emit prison event: " + ex.Message);
            }
        }

        // mute / unmute

        private void MutePlayer(BasePlayer player) => ConsoleSystem.Run(ConsoleSystem.Option.Server, $"mute {player.UserIDString}");

        private void UnmutePlayer(ulong userId)
        {
            // Works for offline releases too.
            ConsoleSystem.Run(ConsoleSystem.Option.Server, $"unmute {userId}");
        }

        private void UnmutePlayer(BasePlayer player)
        {
            if (player == null) return;

            ulong userId = player.userID;
            UnmutePlayer(userId);

            // Re-assert shortly after release in case the native command was dropped while
            // the player's connection/state was settling. Never do this if they were jailed again.
            timer.Once(0.5f, () =>
            {
                if (jailedPlayers.ContainsKey(userId)) return;
                ConsoleSystem.Run(ConsoleSystem.Option.Server, $"unmute {userId}");
            });
        }

        // data persistence

        private void SaveData()        => Interface.Oxide.DataFileSystem.WriteObject(DataFile,        jailedPlayers);
        private void SavePendingData() => Interface.Oxide.DataFileSystem.WriteObject(PendingDataFile, pendingJails);
        private void SaveReasonsData() => Interface.Oxide.DataFileSystem.WriteObject(ReasonsDataFile, jailReasons);
        private void SaveTimesData()   => Interface.Oxide.DataFileSystem.WriteObject(TimesDataFile,   jailStartTimes);
        private void SaveDurationsData() => Interface.Oxide.DataFileSystem.WriteObject(DurationsDataFile, jailDurations);
        private void SaveOffenseCountsData() => Interface.Oxide.DataFileSystem.WriteObject(OffenseCountsDataFile, jailOffenseCounts);
    }
}