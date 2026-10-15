using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using Timer = Oxide.Plugins.Timer;

namespace Carbon.Plugins;

[Info("FiveHeadsAIBridge", "5HeadNN", "1.1.0")]
[Description("Safe outbound bridge between 5Heads Arena / CoreLobby and the Railway AI Manager.")]
public class FiveHeadsAIBridge : CarbonPlugin
{
    private const int ProtocolVersion = 1;
    private const string AdminPermission = "fiveheadsai.admin";
    private const string DataFileName = "FiveHeadsAIBridge";
    private const string PopupUiName = "FiveHeadsAI.Popup";

    [PluginReference]
    private Plugin CoreLobby;

    private Configuration _config;
    private StoredData _data;
    private string _instanceId;
    private bool _serverInitialized;
    private bool _heartbeatInFlight;
    private bool _eventBatchInFlight;
    private bool _commandPollInFlight;

    private Timer _heartbeatTimer;
    private Timer _eventFlushTimer;
    private Timer _commandPollTimer;
    private Timer _resultFlushTimer;

    private readonly List<BridgeEvent> _eventQueue = new();
    private readonly List<CommandResultEnvelope> _resultQueue = new();
    private readonly HashSet<string> _resultInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processedCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string> _knownPlayerModes = new();
    private readonly Dictionary<ulong, double> _connectedAt = new();
    private readonly Dictionary<ulong, double> _aiQuestionCooldowns = new();
    private Dictionary<string, Func<AiCommand, CommandResultEnvelope>> _commandHandlers;

    #region Configuration

    private class KnownGamemodeConfig
    {
        public string Key;
        public string DisplayName;
        public string PluginName;
    }

    private class ApprovedEventConfig
    {
        public string PluginName;
        public int MaxDurationMinutes = 60;
    }

    private class Configuration
    {
        [JsonProperty("Enabled")]
        public bool Enabled = false;

        [JsonProperty("Server ID")]
        public string ServerId = "5heads-arena-main";

        [JsonProperty("API Base URL")]
        public string ApiBaseUrl = "https://YOUR-RAILWAY-SERVICE.up.railway.app";

        [JsonProperty("API Secret")]
        public string ApiSecret = "CHANGE_ME";

        [JsonProperty("Allow insecure HTTP (local testing only)")]
        public bool AllowInsecureHttp = false;

        [JsonProperty("Heartbeat seconds")]
        public float HeartbeatSeconds = 10f;

        [JsonProperty("Command poll seconds")]
        public float CommandPollSeconds = 2f;

        [JsonProperty("Event flush seconds")]
        public float EventFlushSeconds = 5f;

        [JsonProperty("Command result retry seconds")]
        public float ResultRetrySeconds = 5f;

        [JsonProperty("Network timeout milliseconds")]
        public float NetworkTimeoutMilliseconds = 5000f;

        [JsonProperty("Max queued events")]
        public int MaxQueuedEvents = 500;

        [JsonProperty("Max queued command results")]
        public int MaxQueuedResults = 250;

        [JsonProperty("Max commands per poll")]
        public int MaxCommandsPerPoll = 10;

        [JsonProperty("Remember processed command IDs")]
        public int RememberProcessedCommandIds = 500;

        [JsonProperty("Include player names in telemetry")]
        public bool IncludePlayerNames = true;

        [JsonProperty("Enable player /ai command")]
        public bool EnablePlayerAiCommand = true;

        [JsonProperty("Player /ai cooldown seconds")]
        public float PlayerAiCooldownSeconds = 10f;

        [JsonProperty("Max player /ai question characters")]
        public int MaxPlayerAiQuestionCharacters = 200;

        [JsonProperty("Allowed actions")]
        public List<string> AllowedActions = new()
        {
            "send_lobby_announcement",
            "send_lobby_popup",
            "send_player_message",
            "get_status_snapshot",
            "start_event",
            "stop_event"
        };

        [JsonProperty("Critical plugins")]
        public List<string> CriticalPlugins = new()
        {
            "CoreLobby"
        };

        [JsonProperty("Known gamemodes")]
        public List<KnownGamemodeConfig> KnownGamemodes = new()
        {
            new KnownGamemodeConfig { Key = "GunGame",      DisplayName = "Gun Game",      PluginName = "GunGame" },
            new KnownGamemodeConfig { Key = "AKFFA",        DisplayName = "AK FFA",        PluginName = "AKFFA" },
            new KnownGamemodeConfig { Key = "AKTDM",        DisplayName = "TDM AK",        PluginName = "AKTDM" },
            new KnownGamemodeConfig { Key = "PrimitiveFFA", DisplayName = "Primitive FFA", PluginName = "PrimitiveFFA" },
            new KnownGamemodeConfig { Key = "PublicDuels",  DisplayName = "Public Duels",  PluginName = "PublicDuels" },
            new KnownGamemodeConfig { Key = "AimTrain",     DisplayName = "Aim Train",     PluginName = "AimTrain" },
            new KnownGamemodeConfig { Key = "FragIsland",   DisplayName = "Frag Island",   PluginName = "FragIsland" },
            new KnownGamemodeConfig { Key = "MazePvP",      DisplayName = "Maze PvP",      PluginName = "MazePvP" },
            new KnownGamemodeConfig { Key = "KOTH",         DisplayName = "Hardpoint",      PluginName = "KOTH" }
        };

        [JsonProperty("Approved AI events")]
        public Dictionary<string, ApprovedEventConfig> ApprovedEvents =
            new(StringComparer.OrdinalIgnoreCase);
    }

    protected override void LoadDefaultConfig()
    {
        _config = new Configuration();
    }

    protected override void LoadConfig()
    {
        base.LoadConfig();

        try
        {
            _config = Config.ReadObject<Configuration>() ?? new Configuration();
        }
        catch
        {
            PrintWarning("Invalid config detected; generating a fresh FiveHeadsAIBridge config.");
            _config = new Configuration();
        }

        _config.AllowedActions ??= new List<string>();
        _config.CriticalPlugins ??= new List<string>();
        _config.KnownGamemodes ??= new List<KnownGamemodeConfig>();
        _config.ApprovedEvents = _config.ApprovedEvents == null
            ? new Dictionary<string, ApprovedEventConfig>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ApprovedEventConfig>(
                _config.ApprovedEvents,
                StringComparer.OrdinalIgnoreCase);

        _config.HeartbeatSeconds = Mathf.Clamp(_config.HeartbeatSeconds, 5f, 300f);
        _config.CommandPollSeconds = Mathf.Clamp(_config.CommandPollSeconds, 1f, 60f);
        _config.EventFlushSeconds = Mathf.Clamp(_config.EventFlushSeconds, 1f, 60f);
        _config.ResultRetrySeconds = Mathf.Clamp(_config.ResultRetrySeconds, 2f, 120f);
        _config.NetworkTimeoutMilliseconds = Mathf.Clamp(_config.NetworkTimeoutMilliseconds, 1000f, 30000f);
        _config.MaxQueuedEvents = Mathf.Clamp(_config.MaxQueuedEvents, 50, 5000);
        _config.MaxQueuedResults = Mathf.Clamp(_config.MaxQueuedResults, 25, 2000);
        _config.MaxCommandsPerPoll = Mathf.Clamp(_config.MaxCommandsPerPoll, 1, 50);
        _config.RememberProcessedCommandIds = Mathf.Clamp(_config.RememberProcessedCommandIds, 50, 5000);
        _config.PlayerAiCooldownSeconds = Mathf.Clamp(_config.PlayerAiCooldownSeconds, 3f, 120f);
        _config.MaxPlayerAiQuestionCharacters = Mathf.Clamp(_config.MaxPlayerAiQuestionCharacters, 40, 500);

        SaveConfig();
    }

    protected override void SaveConfig() => Config.WriteObject(_config, true);

    #endregion

    #region Data

    private class StoredData
    {
        public List<string> ProcessedCommandIds = new();
    }

    private void LoadData()
    {
        try
        {
            _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFileName) ?? new StoredData();
        }
        catch
        {
            _data = new StoredData();
        }

        _data.ProcessedCommandIds ??= new List<string>();

        foreach (var commandId in _data.ProcessedCommandIds)
        {
            if (!string.IsNullOrWhiteSpace(commandId))
                _processedCommandIds.Add(commandId);
        }
    }

    private void SaveData()
    {
        _data ??= new StoredData();
        var validIds = _data.ProcessedCommandIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        int keep = Mathf.Max(50, _config.RememberProcessedCommandIds);
        _data.ProcessedCommandIds = validIds
            .Skip(Math.Max(0, validIds.Count - keep))
            .ToList();

        Interface.Oxide.DataFileSystem.WriteObject(DataFileName, _data);
    }

    private void RememberProcessedCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || _processedCommandIds.Contains(commandId))
            return;

        _processedCommandIds.Add(commandId);
        _data.ProcessedCommandIds.Add(commandId);

        int max = Mathf.Max(50, _config.RememberProcessedCommandIds);
        while (_data.ProcessedCommandIds.Count > max)
        {
            var oldest = _data.ProcessedCommandIds[0];
            _data.ProcessedCommandIds.RemoveAt(0);
            _processedCommandIds.Remove(oldest);
        }

        SaveData();
    }

    #endregion

    #region DTOs

    private class PlayerRef
    {
        [JsonProperty("steam_id")] public string SteamId;
        [JsonProperty("name")] public string Name;
    }

    private class BridgeEvent
    {
        [JsonProperty("protocol_version")] public int ProtocolVersion = FiveHeadsAIBridge.ProtocolVersion;
        [JsonProperty("event_id")] public string EventId;
        [JsonProperty("server_id")] public string ServerId;
        [JsonProperty("instance_id")] public string InstanceId;
        [JsonProperty("event_type")] public string EventType;
        [JsonProperty("occurred_at")] public string OccurredAt;
        [JsonProperty("player", NullValueHandling = NullValueHandling.Ignore)] public PlayerRef Player;
        [JsonProperty("data")] public JObject Data = new();
    }

    private class EventBatchEnvelope
    {
        [JsonProperty("protocol_version")] public int ProtocolVersion = FiveHeadsAIBridge.ProtocolVersion;
        [JsonProperty("events")] public List<BridgeEvent> Events;
    }

    private class HeartbeatServer
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("online")] public int Online;
        [JsonProperty("max_players")] public int MaxPlayers;
        [JsonProperty("lobby_players")] public int LobbyPlayers;
        [JsonProperty("uptime_seconds")] public long UptimeSeconds;
    }

    private class HeartbeatSoftware
    {
        [JsonProperty("carbon")] public bool Carbon = true;
        [JsonProperty("bridge_version")] public string BridgeVersion;
        [JsonProperty("corelobby_loaded")] public bool CoreLobbyLoaded;
        [JsonProperty("corelobby_version", NullValueHandling = NullValueHandling.Ignore)] public string CoreLobbyVersion;
    }

    private class HeartbeatGamemode
    {
        [JsonProperty("key")] public string Key;
        [JsonProperty("display_name")] public string DisplayName;
        [JsonProperty("plugin_name")] public string PluginName;
        [JsonProperty("players")] public int Players;
        [JsonProperty("queue")] public int Queue;
        [JsonProperty("status")] public string Status;
    }

    private class HeartbeatHealth
    {
        [JsonProperty("bridge_ok")] public bool BridgeOk = true;
        [JsonProperty("corelobby_ok")] public bool CoreLobbyOk;
        [JsonProperty("queued_events")] public int QueuedEvents;
        [JsonProperty("queued_command_results")] public int QueuedCommandResults;
        [JsonProperty("missing_critical_plugins")] public List<string> MissingCriticalPlugins = new();
    }

    private class HeartbeatEnvelope
    {
        [JsonProperty("protocol_version")] public int ProtocolVersion = FiveHeadsAIBridge.ProtocolVersion;
        [JsonProperty("server_id")] public string ServerId;
        [JsonProperty("instance_id")] public string InstanceId;
        [JsonProperty("sent_at")] public string SentAt;
        [JsonProperty("server")] public HeartbeatServer Server;
        [JsonProperty("software")] public HeartbeatSoftware Software;
        [JsonProperty("gamemodes")] public List<HeartbeatGamemode> Gamemodes;
        [JsonProperty("health")] public HeartbeatHealth Health;
    }

    private class RequestedBy
    {
        [JsonProperty("type")] public string Type;
        [JsonProperty("id")] public string Id;
    }

    private class AiCommand
    {
        [JsonProperty("command_id")] public string CommandId;
        [JsonProperty("action")] public string Action;
        [JsonProperty("issued_at")] public string IssuedAt;
        [JsonProperty("expires_at")] public string ExpiresAt;
        [JsonProperty("requested_by")] public RequestedBy RequestedBy;
        [JsonProperty("reason")] public string Reason;
        [JsonProperty("arguments")] public JObject Arguments = new();
    }

    private class CommandPollResponse
    {
        [JsonProperty("protocol_version")] public int ProtocolVersion;
        [JsonProperty("commands")] public List<AiCommand> Commands = new();
    }

    private class CommandError
    {
        [JsonProperty("code")] public string Code;
        [JsonProperty("message")] public string Message;
    }

    private class CommandResultEnvelope
    {
        [JsonProperty("protocol_version")] public int ProtocolVersion = FiveHeadsAIBridge.ProtocolVersion;
        [JsonProperty("command_id")] public string CommandId;
        [JsonProperty("server_id")] public string ServerId;
        [JsonProperty("status")] public string Status;
        [JsonProperty("finished_at")] public string FinishedAt;
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)] public JObject Result;
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)] public CommandError Error;
    }

    #endregion

    #region Lifecycle

    private void Init()
    {
        permission.RegisterPermission(AdminPermission, this);
        _instanceId = Guid.NewGuid().ToString("N");
        LoadData();
        BuildCommandRegistry();
    }

    private void OnServerInitialized()
    {
        _serverInitialized = true;

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (player == null || !player.IsConnected) continue;
            _connectedAt[(ulong)player.userID] = UnityEngine.Time.realtimeSinceStartupAsDouble;
        }

        RefreshKnownPlayerModes();

        if (!IsNetworkConfigured(out var reason))
        {
            PrintWarning($"FiveHeads AI Bridge is not connected: {reason}");
            PrintWarning("Configure Carbon/configs/FiveHeadsAIBridge.json, then reload the plugin.");
            return;
        }

        StartBridgeTimers();

        QueueEvent("server_started", null, new JObject
        {
            ["bridge_version"] = Version.ToString(),
            ["corelobby_loaded"] = GetCoreLobby() != null
        });

        timer.Once(1f, SendHeartbeat);
        timer.Once(1.5f, PollCommands);
        Puts($"FiveHeads AI Bridge v{Version} started for '{_config.ServerId}'.");
    }

    private void Unload()
    {
        _heartbeatTimer?.Destroy();
        _eventFlushTimer?.Destroy();
        _commandPollTimer?.Destroy();
        _resultFlushTimer?.Destroy();

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (player != null)
                CuiHelper.DestroyUi(player, PopupUiName);
        }

        SaveData();
    }

    private void StartBridgeTimers()
    {
        _heartbeatTimer?.Destroy();
        _eventFlushTimer?.Destroy();
        _commandPollTimer?.Destroy();
        _resultFlushTimer?.Destroy();

        _heartbeatTimer = timer.Every(_config.HeartbeatSeconds, SendHeartbeat);
        _eventFlushTimer = timer.Every(_config.EventFlushSeconds, FlushEvents);
        _commandPollTimer = timer.Every(_config.CommandPollSeconds, PollCommands);
        _resultFlushTimer = timer.Every(_config.ResultRetrySeconds, FlushCommandResults);
    }

    private void OnPlayerConnected(BasePlayer player)
    {
        if (player == null) return;

        _connectedAt[(ulong)player.userID] = UnityEngine.Time.realtimeSinceStartupAsDouble;

        QueueEvent("player_connected", player, new JObject
        {
            ["online_after"] = BasePlayer.activePlayerList.Count
        });
    }

    private void OnPlayerDisconnected(BasePlayer player, string reason)
    {
        if (player == null) return;

        string lastMode = null;
        _knownPlayerModes.TryGetValue((ulong)player.userID, out lastMode);

        long sessionSeconds = 0;
        if (_connectedAt.TryGetValue((ulong)player.userID, out var connectedAt))
            sessionSeconds = Math.Max(0L, (long)(UnityEngine.Time.realtimeSinceStartupAsDouble - connectedAt));

        QueueEvent("player_disconnected", player, new JObject
        {
            ["reason"] = SafeTelemetryText(reason, 120),
            ["session_seconds"] = sessionSeconds,
            ["last_gamemode"] = lastMode
        });

        _knownPlayerModes.Remove((ulong)player.userID);
        _connectedAt.Remove((ulong)player.userID);
        _aiQuestionCooldowns.Remove((ulong)player.userID);
        CuiHelper.DestroyUi(player, PopupUiName);
    }

    private void OnCoreLobbyGamemodeStateChanged(ulong playerId, bool inGamemode)
    {
        var player = BasePlayer.FindByID(playerId);

        if (inGamemode)
        {
            string mode = GetCoreLobbyPlayerMode(playerId);
            if (!string.IsNullOrWhiteSpace(mode))
                _knownPlayerModes[playerId] = mode;

            if (player != null)
                CuiHelper.DestroyUi(player, PopupUiName);

            QueueEvent("gamemode_joined", player, new JObject
            {
                ["gamemode"] = mode ?? "unknown"
            });
        }
        else
        {
            string mode = null;
            _knownPlayerModes.TryGetValue(playerId, out mode);
            _knownPlayerModes.Remove(playerId);

            QueueEvent("gamemode_left", player, new JObject
            {
                ["gamemode"] = mode ?? "unknown"
            });
        }
    }

    private void OnPluginLoaded(Plugin plugin)
    {
        if (!_serverInitialized || plugin == null || plugin == this) return;

        if (string.Equals(plugin.Name, "CoreLobby", StringComparison.OrdinalIgnoreCase))
            CoreLobby = plugin;

        QueueEvent("plugin_loaded", null, new JObject
        {
            ["plugin"] = SafeTelemetryText(plugin.Name, 80),
            ["version"] = plugin.Version.ToString()
        });
    }

    private void OnPluginUnloaded(Plugin plugin)
    {
        if (!_serverInitialized || plugin == null || plugin == this) return;

        QueueEvent("plugin_unloaded", null, new JObject
        {
            ["plugin"] = SafeTelemetryText(plugin.Name, 80),
            ["version"] = plugin.Version.ToString()
        });

        if (string.Equals(plugin.Name, "CoreLobby", StringComparison.OrdinalIgnoreCase))
            CoreLobby = null;
    }

    #endregion

    #region CoreLobby state

    private Plugin GetCoreLobby()
    {
        if (CoreLobby != null) return CoreLobby;

        try { CoreLobby = plugins.Find("CoreLobby"); }
        catch { }

        return CoreLobby;
    }

    private string GetCoreLobbyPlayerMode(ulong playerId)
    {
        try
        {
            return GetCoreLobby()?.Call("GetPlayerGamemode", playerId) as string;
        }
        catch
        {
            return null;
        }
    }

    private Dictionary<ulong, string> GetGamemodePlayers()
    {
        try
        {
            var result = GetCoreLobby()?.Call("GetAllGamemodePlayers");

            if (result is Dictionary<ulong, string> dictionary)
                return new Dictionary<ulong, string>(dictionary);

            if (result is IDictionary<ulong, string> generic)
                return generic.ToDictionary(x => x.Key, x => x.Value);
        }
        catch { }

        return new Dictionary<ulong, string>();
    }

    private void RefreshKnownPlayerModes()
    {
        var activeIds = new HashSet<ulong>(
            BasePlayer.activePlayerList
                .Where(x => x != null && x.IsConnected)
                .Select(x => (ulong)x.userID));

        var modes = GetGamemodePlayers();

        foreach (var pair in modes)
        {
            if (activeIds.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                _knownPlayerModes[pair.Key] = pair.Value;
        }

        foreach (var playerId in _knownPlayerModes.Keys.Where(x => !activeIds.Contains(x)).ToList())
            _knownPlayerModes.Remove(playerId);
    }

    private bool IsLobbyPlayer(BasePlayer player)
    {
        if (player == null || player.IsDestroyed || !player.IsConnected)
            return false;

        // Fail closed: if CoreLobby is unavailable, the bridge will not guess that
        // somebody is in the lobby and display AI UI over a gamemode.
        if (GetCoreLobby() == null)
            return false;

        return string.IsNullOrWhiteSpace(GetCoreLobbyPlayerMode((ulong)player.userID));
    }

    #endregion

    #region Heartbeat

    private HeartbeatEnvelope BuildHeartbeat()
    {
        RefreshKnownPlayerModes();

        var activePlayers = BasePlayer.activePlayerList
            .Where(x => x != null && x.IsConnected)
            .ToList();

        var activeIds = new HashSet<ulong>(activePlayers.Select(x => (ulong)x.userID));
        var trackedModes = GetGamemodePlayers()
            .Where(x => activeIds.Contains(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        var counts = trackedModes
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var gamemodes = new List<HeartbeatGamemode>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var known in _config.KnownGamemodes ?? new List<KnownGamemodeConfig>())
        {
            if (known == null || string.IsNullOrWhiteSpace(known.Key)) continue;

            int players = counts.TryGetValue(known.Key, out var count) ? count : 0;
            bool pluginOnline = string.IsNullOrWhiteSpace(known.PluginName) || plugins.Find(known.PluginName) != null;

            gamemodes.Add(new HeartbeatGamemode
            {
                Key = known.Key,
                DisplayName = string.IsNullOrWhiteSpace(known.DisplayName) ? known.Key : known.DisplayName,
                PluginName = known.PluginName,
                Players = players,
                Queue = 0,
                Status = pluginOnline ? "online" : "offline"
            });

            added.Add(known.Key);
        }

        foreach (var pair in counts)
        {
            if (added.Contains(pair.Key)) continue;

            gamemodes.Add(new HeartbeatGamemode
            {
                Key = pair.Key,
                DisplayName = pair.Key,
                PluginName = pair.Key,
                Players = pair.Value,
                Queue = 0,
                Status = "online"
            });
        }

        var missingCritical = new List<string>();
        foreach (var pluginName in _config.CriticalPlugins ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(pluginName) && plugins.Find(pluginName) == null)
                missingCritical.Add(pluginName);
        }

        var core = GetCoreLobby();

        return new HeartbeatEnvelope
        {
            ServerId = _config.ServerId,
            InstanceId = _instanceId,
            SentAt = UtcNowIso(),
            Server = new HeartbeatServer
            {
                Name = ConVar.Server.hostname,
                Online = activePlayers.Count,
                MaxPlayers = ConVar.Server.maxplayers,
                LobbyPlayers = Math.Max(0, activePlayers.Count - trackedModes.Count),
                UptimeSeconds = Math.Max(0L, (long)UnityEngine.Time.realtimeSinceStartupAsDouble)
            },
            Software = new HeartbeatSoftware
            {
                BridgeVersion = Version.ToString(),
                CoreLobbyLoaded = core != null,
                CoreLobbyVersion = core != null ? core.Version.ToString() : null
            },
            Gamemodes = gamemodes,
            Health = new HeartbeatHealth
            {
                CoreLobbyOk = core != null,
                QueuedEvents = _eventQueue.Count,
                QueuedCommandResults = _resultQueue.Count,
                MissingCriticalPlugins = missingCritical
            }
        };
    }

    private void SendHeartbeat()
    {
        if (_heartbeatInFlight || !IsNetworkConfigured(out _))
            return;

        _heartbeatInFlight = true;

        var payload = BuildHeartbeat();
        string body = Serialize(payload);
        string path = $"/v1/servers/{Uri.EscapeDataString(_config.ServerId)}/heartbeat";
        string url = BuildUrl(path);

        SendRequest(RequestMethod.POST, url, body, (code, response) =>
        {
            _heartbeatInFlight = false;

            if (!IsHttpSuccess(code))
                PrintNetworkWarning("heartbeat", code, response);
        });
    }

    #endregion

    #region Events

    private void QueueEvent(string eventType, BasePlayer player, JObject data)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(eventType))
            return;

        var evt = new BridgeEvent
        {
            EventId = "evt_" + Guid.NewGuid().ToString("N"),
            ServerId = _config.ServerId,
            InstanceId = _instanceId,
            EventType = eventType,
            OccurredAt = UtcNowIso(),
            Player = player == null
                ? null
                : new PlayerRef
                {
                    SteamId = player.UserIDString,
                    Name = _config.IncludePlayerNames ? SafeTelemetryText(player.displayName, 64) : null
                },
            Data = data ?? new JObject()
        };

        _eventQueue.Add(evt);

        int max = Mathf.Max(50, _config.MaxQueuedEvents);
        while (_eventQueue.Count > max)
            _eventQueue.RemoveAt(0);

        if (_eventQueue.Count >= 20)
            FlushEvents();
    }

    private void FlushEvents()
    {
        if (_eventBatchInFlight || _eventQueue.Count == 0 || !IsNetworkConfigured(out _))
            return;

        var batch = _eventQueue.Take(20).ToList();
        if (batch.Count == 0) return;

        _eventBatchInFlight = true;

        var envelope = new EventBatchEnvelope { Events = batch };
        string body = Serialize(envelope);
        string path = $"/v1/servers/{Uri.EscapeDataString(_config.ServerId)}/events";
        string url = BuildUrl(path);

        SendRequest(RequestMethod.POST, url, body, (code, response) =>
        {
            _eventBatchInFlight = false;

            if (IsHttpSuccess(code))
            {
                var sentIds = new HashSet<string>(batch.Select(x => x.EventId), StringComparer.OrdinalIgnoreCase);
                _eventQueue.RemoveAll(x => sentIds.Contains(x.EventId));
                return;
            }

            PrintNetworkWarning("events", code, response);
        });
    }

    #endregion

    #region Command polling

    private void BuildCommandRegistry()
    {
        _commandHandlers = new Dictionary<string, Func<AiCommand, CommandResultEnvelope>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["send_lobby_announcement"] = HandleLobbyAnnouncement,
            ["send_lobby_popup"] = HandleLobbyPopup,
            ["send_player_message"] = HandlePlayerMessage,
            ["get_status_snapshot"] = HandleStatusSnapshot,
            ["start_event"] = command => HandleApprovedEvent(command, true),
            ["stop_event"] = command => HandleApprovedEvent(command, false)
        };
    }

    private void PollCommands()
    {
        if (_commandPollInFlight || !IsNetworkConfigured(out _))
            return;

        _commandPollInFlight = true;

        int limit = Mathf.Clamp(_config.MaxCommandsPerPoll, 1, 50);
        string path =
            $"/v1/servers/{Uri.EscapeDataString(_config.ServerId)}/commands?limit={limit}";
        string url = BuildUrl(path);

        SendRequest(RequestMethod.GET, url, string.Empty, (code, response) =>
        {
            _commandPollInFlight = false;

            if (!IsHttpSuccess(code))
            {
                PrintNetworkWarning("command poll", code, response);
                return;
            }

            if (string.IsNullOrWhiteSpace(response))
                return;

            CommandPollResponse poll;

            try
            {
                poll = JsonConvert.DeserializeObject<CommandPollResponse>(response);
            }
            catch (Exception ex)
            {
                PrintWarning($"AI command poll returned invalid JSON: {ex.Message}");
                return;
            }

            if (poll?.Commands == null || poll.Commands.Count == 0)
                return;

            foreach (var command in poll.Commands.Take(limit))
            {
                var result = ExecuteCommand(command);
                SubmitCommandResult(result);
            }
        });
    }

    private CommandResultEnvelope ExecuteCommand(AiCommand command)
    {
        if (command == null)
            return Reject(null, "INVALID_COMMAND", "Command payload was null.");

        if (string.IsNullOrWhiteSpace(command.CommandId) || command.CommandId.Length > 128)
            return Reject(command.CommandId, "INVALID_COMMAND", "command_id is missing or invalid.");

        if (_processedCommandIds.Contains(command.CommandId))
        {
            var queuedOriginal = _resultQueue.FirstOrDefault(x =>
                string.Equals(x.CommandId, command.CommandId, StringComparison.OrdinalIgnoreCase));

            if (queuedOriginal != null)
                return queuedOriginal;

            return Result(command.CommandId, "duplicate", new JObject
            {
                ["message"] = "This command ID was already processed."
            });
        }

        if (string.IsNullOrWhiteSpace(command.Action) || command.Action.Length > 64)
            return Reject(command.CommandId, "INVALID_COMMAND", "action is missing or invalid.");

        if (IsCommandExpired(command))
        {
            RememberProcessedCommand(command.CommandId);
            return Reject(command.CommandId, "COMMAND_EXPIRED", "Command expiry time was exceeded.", "expired");
        }

        var configuredAllowed = new HashSet<string>(
            _config.AllowedActions ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        if (!configuredAllowed.Contains(command.Action))
        {
            RememberProcessedCommand(command.CommandId);
            return Reject(command.CommandId, "ACTION_DISABLED",
                "Action is disabled in FiveHeadsAIBridge config.");
        }

        if (!_commandHandlers.TryGetValue(command.Action, out var handler))
        {
            RememberProcessedCommand(command.CommandId);
            return Reject(command.CommandId, "ACTION_NOT_ALLOWED",
                "Action does not exist in the compiled Carbon allowlist.");
        }

        // Mark before execution: a network retry must never execute a side effect twice.
        RememberProcessedCommand(command.CommandId);

        try
        {
            return handler(command);
        }
        catch (Exception ex)
        {
            PrintError($"AI action '{command.Action}' ({command.CommandId}) failed: {ex.Message}");
            return Fail(command.CommandId, "EXECUTION_FAILED", "The Carbon action threw an exception.");
        }
    }

    private bool IsCommandExpired(AiCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ExpiresAt))
            return true;

        if (!DateTimeOffset.TryParse(command.ExpiresAt, out var expires))
            return true;

        return DateTimeOffset.UtcNow > expires.ToUniversalTime();
    }

    #endregion

    #region Command handlers

    private CommandResultEnvelope HandleLobbyAnnouncement(AiCommand command)
    {
        string message = GetTextArgument(command, "message", 1, 180, out var error);
        if (error != null) return error;

        int reached = 0;

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (!IsLobbyPlayer(player)) continue;

            player.ChatMessage(
                $"<color=#A855F7><b>5HEADS ARENA</b></color> {EscapePlayerText(message)}");
            reached++;
        }

        QueueEvent("announcement_sent", null, new JObject
        {
            ["source"] = "ai",
            ["players_reached"] = reached
        });

        return Result(command.CommandId, "succeeded", new JObject
        {
            ["players_reached"] = reached
        });
    }

    private CommandResultEnvelope HandleLobbyPopup(AiCommand command)
    {
        string title = GetTextArgument(command, "title", 1, 60, out var error);
        if (error != null) return error;

        string message = GetTextArgument(command, "message", 1, 100, out error);
        if (error != null) return error;

        string subtext = GetOptionalTextArgument(command, "subtext", 160);
        float duration = GetFloatArgument(command, "duration_seconds", 10f);

        if (duration < 4f || duration > 20f)
            return Reject(command.CommandId, "OUT_OF_RANGE",
                "duration_seconds must be between 4 and 20 seconds.");

        int reached = 0;

        foreach (var player in BasePlayer.activePlayerList)
        {
            if (!IsLobbyPlayer(player)) continue;

            ShowLobbyPopup(player, title, message, subtext, duration);
            reached++;
        }

        QueueEvent("promotion_shown", null, new JObject
        {
            ["source"] = "ai",
            ["players_reached"] = reached,
            ["duration_seconds"] = duration
        });

        return Result(command.CommandId, "succeeded", new JObject
        {
            ["players_reached"] = reached
        });
    }

    private CommandResultEnvelope HandlePlayerMessage(AiCommand command)
    {
        string steamIdText = GetTextArgument(command, "steam_id", 17, 24, out var error, false);
        if (error != null) return error;

        if (!ulong.TryParse(steamIdText, out var steamId))
            return Reject(command.CommandId, "INVALID_ARGUMENTS", "steam_id must be a numeric Steam ID.");

        string message = GetTextArgument(command, "message", 1, 180, out error);
        if (error != null) return error;

        var player = BasePlayer.FindByID(steamId);
        if (player == null || !player.IsConnected)
            return Reject(command.CommandId, "PLAYER_NOT_CONNECTED", "Player is not connected.");

        player.ChatMessage(
            $"<color=#A855F7><b>5HEADS AI</b></color> {EscapePlayerText(message)}");

        return Result(command.CommandId, "succeeded", new JObject
        {
            ["steam_id"] = steamId.ToString()
        });
    }

    private CommandResultEnvelope HandleStatusSnapshot(AiCommand command)
    {
        var heartbeat = BuildHeartbeat();
        return Result(command.CommandId, "succeeded", JObject.FromObject(heartbeat));
    }

    private CommandResultEnvelope HandleApprovedEvent(AiCommand command, bool start)
    {
        string eventId = GetTextArgument(command, "event_id", 1, 64, out var error, false);
        if (error != null) return error;

        if (_config.ApprovedEvents == null ||
            !_config.ApprovedEvents.TryGetValue(eventId, out var target) ||
            target == null ||
            string.IsNullOrWhiteSpace(target.PluginName))
        {
            return Reject(command.CommandId, "EVENT_NOT_FOUND",
                "Event is not present in the Carbon approved-event registry.");
        }

        var plugin = plugins.Find(target.PluginName);
        if (plugin == null)
            return Reject(command.CommandId, "PLUGIN_NOT_FOUND",
                $"Approved event plugin '{target.PluginName}' is not loaded.");

        object callResult;

        if (start)
        {
            int durationMinutes = Mathf.RoundToInt(GetFloatArgument(command, "duration_minutes", 30f));
            int maxDuration = Mathf.Clamp(target.MaxDurationMinutes, 1, 240);

            if (durationMinutes < 1 || durationMinutes > maxDuration)
                return Reject(command.CommandId, "OUT_OF_RANGE",
                    $"duration_minutes must be between 1 and {maxDuration}.");

            // Approved event plugins can opt in by implementing:
            // [HookMethod("AI_StartEvent")] object AI_StartEvent(int durationMinutes)
            callResult = plugin.Call("AI_StartEvent", durationMinutes);
        }
        else
        {
            // Approved event plugins can opt in by implementing:
            // [HookMethod("AI_StopEvent")] object AI_StopEvent()
            callResult = plugin.Call("AI_StopEvent");
        }

        if (callResult is bool ok && !ok)
            return Fail(command.CommandId, "PLUGIN_CALL_FAILED",
                $"Plugin '{target.PluginName}' rejected the AI event action.");

        QueueEvent(start ? "event_started" : "event_stopped", null, new JObject
        {
            ["event_id"] = eventId,
            ["plugin"] = target.PluginName,
            ["source"] = "ai"
        });

        return Result(command.CommandId, "succeeded", new JObject
        {
            ["event_id"] = eventId,
            ["plugin"] = target.PluginName
        });
    }

    #endregion

    #region Lobby popup UI

    private void ShowLobbyPopup(BasePlayer player, string title, string message, string subtext, float seconds)
    {
        if (!IsLobbyPlayer(player)) return;

        CuiHelper.DestroyUi(player, PopupUiName);

        var ui = new CuiElementContainer();

        var frame = ui.Add(new CuiPanel
        {
            Image = { Color = "0.58 0.16 1.00 0.98" },
            RectTransform =
            {
                AnchorMin = "0.315 0.855",
                AnchorMax = "0.685 0.945"
            },
            CursorEnabled = false
        }, "Overlay", PopupUiName);

        var body = ui.Add(new CuiPanel
        {
            Image = { Color = "0.018 0.019 0.032 0.965" },
            RectTransform =
            {
                AnchorMin = "0 0",
                AnchorMax = "1 1",
                OffsetMin = "2 2",
                OffsetMax = "-2 -2"
            }
        }, frame);

        ui.Add(new CuiPanel
        {
            Image = { Color = "0.69 0.20 1.00 1" },
            RectTransform =
            {
                AnchorMin = "0 0",
                AnchorMax = "0 1",
                OffsetMax = "6 0"
            }
        }, body);

        ui.Add(new CuiLabel
        {
            Text =
            {
                Text = EscapePlayerText(title),
                FontSize = 15,
                Align = TextAnchor.MiddleLeft,
                Color = "0.965 0.955 0.995 1",
                Font = "robotocondensed-bold.ttf"
            },
            RectTransform =
            {
                AnchorMin = "0 0.58",
                AnchorMax = "1 1",
                OffsetMin = "22 0",
                OffsetMax = "-18 0"
            }
        }, body);

        ui.Add(new CuiLabel
        {
            Text =
            {
                Text = EscapePlayerText(message),
                FontSize = 13,
                Align = TextAnchor.MiddleLeft,
                Color = "0.69 0.20 1.00 1",
                Font = "robotocondensed-bold.ttf"
            },
            RectTransform =
            {
                AnchorMin = "0 0.30",
                AnchorMax = "1 0.66",
                OffsetMin = "22 0",
                OffsetMax = "-18 0"
            }
        }, body);

        ui.Add(new CuiLabel
        {
            Text =
            {
                Text = EscapePlayerText(subtext ?? string.Empty),
                FontSize = 10,
                Align = TextAnchor.MiddleLeft,
                Color = "0.70 0.66 0.79 1",
                Font = "robotocondensed-regular.ttf"
            },
            RectTransform =
            {
                AnchorMin = "0 0",
                AnchorMax = "1 0.34",
                OffsetMin = "22 2",
                OffsetMax = "-18 0"
            }
        }, body);

        CuiHelper.AddUi(player, ui);

        timer.Once(seconds, () =>
        {
            if (player != null && !player.IsDestroyed)
                CuiHelper.DestroyUi(player, PopupUiName);
        });
    }

    #endregion

    #region Command results

    private CommandResultEnvelope Result(string commandId, string status, JObject result = null)
    {
        return new CommandResultEnvelope
        {
            CommandId = commandId,
            ServerId = _config.ServerId,
            Status = status,
            FinishedAt = UtcNowIso(),
            Result = result
        };
    }

    private CommandResultEnvelope Reject(
        string commandId,
        string code,
        string message,
        string status = "rejected")
    {
        return new CommandResultEnvelope
        {
            CommandId = commandId,
            ServerId = _config.ServerId,
            Status = status,
            FinishedAt = UtcNowIso(),
            Error = new CommandError
            {
                Code = code,
                Message = message
            }
        };
    }

    private CommandResultEnvelope Fail(string commandId, string code, string message)
    {
        return Reject(commandId, code, message, "failed");
    }

    private void SubmitCommandResult(CommandResultEnvelope result)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.CommandId))
            return;

        if (_resultQueue.Any(x =>
                string.Equals(x.CommandId, result.CommandId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Status, result.Status, StringComparison.OrdinalIgnoreCase)))
            return;

        _resultQueue.Add(result);

        int max = Mathf.Max(25, _config.MaxQueuedResults);
        while (_resultQueue.Count > max)
            _resultQueue.RemoveAt(0);

        TryPostCommandResult(result);
    }

    private void FlushCommandResults()
    {
        if (_resultQueue.Count == 0 || !IsNetworkConfigured(out _))
            return;

        foreach (var result in _resultQueue.Take(10).ToList())
            TryPostCommandResult(result);
    }

    private void TryPostCommandResult(CommandResultEnvelope result)
    {
        if (result == null ||
            string.IsNullOrWhiteSpace(result.CommandId) ||
            _resultInFlight.Contains(result.CommandId) ||
            !IsNetworkConfigured(out _))
            return;

        _resultInFlight.Add(result.CommandId);

        string body = Serialize(result);
        string path = $"/v1/servers/{Uri.EscapeDataString(_config.ServerId)}/command-results";
        string url = BuildUrl(path);

        SendRequest(RequestMethod.POST, url, body, (code, response) =>
        {
            _resultInFlight.Remove(result.CommandId);

            if (IsHttpSuccess(code))
            {
                _resultQueue.Remove(result);
                return;
            }

            PrintNetworkWarning("command result", code, response);
        });
    }

    #endregion

    #region HTTP + authentication

    private bool IsNetworkConfigured(out string reason)
    {
        reason = null;

        if (_config == null || !_config.Enabled)
        {
            reason = "bridge is disabled";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.ServerId))
        {
            reason = "Server ID is empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiBaseUrl) ||
            _config.ApiBaseUrl.Contains("YOUR-RAILWAY-SERVICE", StringComparison.OrdinalIgnoreCase))
        {
            reason = "API Base URL has not been configured";
            return false;
        }

        if (!Uri.TryCreate(_config.ApiBaseUrl, UriKind.Absolute, out var uri))
        {
            reason = "API Base URL is invalid";
            return false;
        }

        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !_config.AllowInsecureHttp)
        {
            reason = "API Base URL must use HTTPS";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiSecret) ||
            _config.ApiSecret.Equals("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
            _config.ApiSecret.Length < 32)
        {
            reason = "API Secret must be changed to a random value of at least 32 characters";
            return false;
        }

        return true;
    }

    private string BuildUrl(string path)
    {
        return _config.ApiBaseUrl.TrimEnd('/') + path;
    }

    private void SendRequest(
        RequestMethod method,
        string url,
        string body,
        Action<int, string> callback)
    {
        string payload = body ?? string.Empty;
        var headers = BuildSignedHeaders(method, url, payload);

        webrequest.Enqueue(
            url,
            method == RequestMethod.GET ? null : payload,
            (code, response) => callback?.Invoke(code, response),
            this,
            method,
            headers,
            _config.NetworkTimeoutMilliseconds);
    }

    private Dictionary<string, string> BuildSignedHeaders(
        RequestMethod method,
        string url,
        string body)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Guid.NewGuid().ToString("N");
        string bodyHash = Sha256Hex(body ?? string.Empty);

        var uri = new Uri(url);
        string requestTarget = uri.PathAndQuery;
        string methodName = method.ToString().ToUpperInvariant();

        string canonical =
            methodName + "\n" +
            requestTarget + "\n" +
            timestamp + "\n" +
            nonce + "\n" +
            bodyHash;

        string signature = HmacSha256Hex(_config.ApiSecret, canonical);

        return new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/json",
            ["X-5H-Server-ID"] = _config.ServerId,
            ["X-5H-Timestamp"] = timestamp.ToString(),
            ["X-5H-Nonce"] = nonce,
            ["X-5H-Body-SHA256"] = bodyHash,
            ["X-5H-Signature"] = signature,
            ["X-5H-Protocol-Version"] = ProtocolVersion.ToString()
        };
    }

    private static string Sha256Hex(string value)
    {
        using var sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BytesToHex(bytes);
    }

    private static string HmacSha256Hex(string secret, string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? string.Empty));
        byte[] bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return BytesToHex(bytes);
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static bool IsHttpSuccess(int code) => code >= 200 && code <= 299;

    private void PrintNetworkWarning(string operation, int code, string response)
    {
        string safeResponse = SafeTelemetryText(response, 180);
        PrintWarning(
            $"AI bridge {operation} failed: HTTP {code}" +
            (string.IsNullOrWhiteSpace(safeResponse) ? string.Empty : $" | {safeResponse}"));
    }

    #endregion

    #region Argument validation

    private string GetTextArgument(
        AiCommand command,
        string key,
        int minLength,
        int maxLength,
        out CommandResultEnvelope error,
        bool sanitize = true)
    {
        error = null;

        string value = command?.Arguments?[key]?.Type == JTokenType.String
            ? command.Arguments[key].Value<string>()
            : null;

        if (value == null)
        {
            error = Reject(command?.CommandId, "INVALID_ARGUMENTS", $"'{key}' is required.");
            return null;
        }

        value = value.Trim();

        if (value.Length < minLength || value.Length > maxLength)
        {
            error = Reject(command?.CommandId, "INVALID_ARGUMENTS",
                $"'{key}' must be between {minLength} and {maxLength} characters.");
            return null;
        }

        if (value.Any(char.IsControl))
        {
            error = Reject(command?.CommandId, "INVALID_ARGUMENTS",
                $"'{key}' contains control characters.");
            return null;
        }

        return sanitize ? EscapePlayerText(value) : value;
    }

    private string GetOptionalTextArgument(AiCommand command, string key, int maxLength)
    {
        var token = command?.Arguments?[key];
        if (token == null || token.Type == JTokenType.Null)
            return string.Empty;

        string value = token.Type == JTokenType.String ? token.Value<string>() : string.Empty;
        value = (value ?? string.Empty).Trim();

        if (value.Length > maxLength)
            value = value.Substring(0, maxLength);

        return EscapePlayerText(value);
    }

    private float GetFloatArgument(AiCommand command, string key, float fallback)
    {
        var token = command?.Arguments?[key];
        if (token == null) return fallback;

        if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            return token.Value<float>();

        if (token.Type == JTokenType.String &&
            float.TryParse(token.Value<string>(), out var parsed))
            return parsed;

        return fallback;
    }

    private static string EscapePlayerText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return text
            .Replace("<", "‹")
            .Replace(">", "›")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static string SafeTelemetryText(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        string safe = text
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();

        return safe.Length <= maxLength ? safe : safe.Substring(0, maxLength);
    }

    private static string Serialize(object value)
    {
        return JsonConvert.SerializeObject(value, Formatting.None,
            new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
    }

    private static string UtcNowIso() =>
        DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    #endregion

    #region Player AI command

    [ChatCommand("ai")]
    private void PlayerAiCommand(BasePlayer player, string command, string[] args)
    {
        if (player == null || !player.IsConnected)
            return;

        if (!_config.EnablePlayerAiCommand)
        {
            player.ChatMessage("<color=#ff7a7a>5Heads AI</color> Player AI questions are currently disabled.");
            return;
        }

        if (!IsNetworkConfigured(out var reason))
        {
            player.ChatMessage($"<color=#ff7a7a>5Heads AI</color> AI is unavailable right now: {EscapePlayerText(reason)}");
            return;
        }

        string question = args == null
            ? string.Empty
            : string.Join(" ", args).Trim();

        if (string.IsNullOrWhiteSpace(question))
        {
            player.ChatMessage("<color=#A855F7><b>5HEADS AI</b></color> Usage: /ai <question>");
            return;
        }

        int maxLength = Mathf.Clamp(_config.MaxPlayerAiQuestionCharacters, 40, 500);
        if (question.Length > maxLength)
        {
            player.ChatMessage(
                $"<color=#ff7a7a>5Heads AI</color> Keep questions under {maxLength} characters.");
            return;
        }

        if (question.Any(char.IsControl))
        {
            player.ChatMessage("<color=#ff7a7a>5Heads AI</color> That question contains invalid characters.");
            return;
        }

        ulong playerId = (ulong)player.userID;
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
        float cooldown = Mathf.Clamp(_config.PlayerAiCooldownSeconds, 3f, 120f);

        if (_aiQuestionCooldowns.TryGetValue(playerId, out var lastAsked))
        {
            double remaining = cooldown - (now - lastAsked);
            if (remaining > 0.05d)
            {
                player.ChatMessage(
                    $"<color=#A855F7>5Heads AI</color> Try again in {Math.Ceiling(remaining):0}s.");
                return;
            }
        }

        _aiQuestionCooldowns[playerId] = now;

        QueueEvent("ai_question", player, new JObject
        {
            ["question"] = SafeTelemetryText(question, maxLength),
            ["source"] = "rust_chat",
            ["gamemode"] = GetCoreLobbyPlayerMode(playerId) ?? "lobby"
        });

        // AI questions are interactive, so do not wait for the normal event flush timer.
        FlushEvents();

        player.ChatMessage(
            "<color=#A855F7><b>5HEADS AI</b></color> Thinking...");
    }

    #endregion

    #region Admin command

    [ChatCommand("aibridge")]
    private void AiBridgeCommand(BasePlayer player, string command, string[] args)
    {
        if (player == null) return;

        if (!player.IsAdmin &&
            !permission.UserHasPermission(player.UserIDString, AdminPermission))
        {
            player.ChatMessage("<color=#ff7a7a>5Heads AI</color> No permission.");
            return;
        }

        string sub = args != null && args.Length > 0
            ? args[0].ToLowerInvariant()
            : "status";

        switch (sub)
        {
            case "status":
            {
                bool configured = IsNetworkConfigured(out var reason);
                player.ChatMessage(
                    "<color=#A855F7><b>5HEADS AI BRIDGE</b></color>\n" +
                    $"Enabled: {(_config.Enabled ? "YES" : "NO")}\n" +
                    $"Configured: {(configured ? "YES" : "NO")}\n" +
                    $"Server ID: {_config.ServerId}\n" +
                    $"Instance: {_instanceId}\n" +
                    $"Events queued: {_eventQueue.Count}\n" +
                    $"Results queued: {_resultQueue.Count}" +
                    (configured ? string.Empty : $"\nReason: {reason}"));
                break;
            }

            case "heartbeat":
                if (!IsNetworkConfigured(out var heartbeatReason))
                {
                    player.ChatMessage($"<color=#ff7a7a>5Heads AI</color> {heartbeatReason}");
                    return;
                }

                SendHeartbeat();
                player.ChatMessage("<color=#A855F7>5Heads AI</color> Heartbeat queued.");
                break;

            case "flush":
                FlushEvents();
                FlushCommandResults();
                player.ChatMessage("<color=#A855F7>5Heads AI</color> Queue flush requested.");
                break;

            case "popup":
                if (!IsLobbyPlayer(player))
                {
                    player.ChatMessage("<color=#ff7a7a>5Heads AI</color> Lobby-only popup test.");
                    return;
                }

                ShowLobbyPopup(
                    player,
                    "5HEADS AI MANAGER",
                    "BRIDGE ONLINE",
                    "Safe command and telemetry layer ready.",
                    8f);
                break;

            default:
                player.ChatMessage(
                    "<color=#A855F7>5Heads AI</color> /aibridge status | heartbeat | flush | popup");
                break;
        }
    }

    #endregion
}