using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Newtonsoft.Json;
using Oxide.Core.Plugins;

namespace Carbon.Plugins;

[Info("FiveHeadsFeedbackMode", "5HeadNN", "1.1.0")]
[Description("Read-only server console lookup of a connected player's CoreLobby mode.")]
public class FiveHeadsFeedbackMode : CarbonPlugin
{
    [PluginReference] private Plugin CoreLobby;

    private void OnServerInitialized()
    {
        Puts("Feedback mode lookup ready. Console command: fiveheads.feedbackmode <SteamID64>");
    }

    private readonly Dictionary<ulong, List<GameEvent>> history = new();
    private class GameEvent
    {
        public string kind;
        public long at;
    }
    private void Record(ulong id, string kind)
    {
        if (!history.TryGetValue(id, out var list)) history[id] = list = new List<GameEvent>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        list.RemoveAll(e => now - e.at > 600000);
        list.Add(new GameEvent { kind = kind, at = now });
        if (list.Count > 8) list.RemoveAt(0);
    }
    private void OnPlayerDeath(BasePlayer player, HitInfo info) { if (player != null) Record((ulong)player.userID, "death"); }
    private void OnPlayerRespawned(BasePlayer player) { if (player != null) Record((ulong)player.userID, "respawn"); }
    private void OnCoreLobbyGamemodeStateChanged(ulong id, bool inGamemode) { Record(id, inGamemode ? "mode_join" : "mode_leave"); }
    private void OnPlayerDisconnected(BasePlayer player, string reason) { if (player != null) history.Remove((ulong)player.userID); }
    private string ReadSingleArenaName(string mode)
    {
        // CoreLobby 2.3.85 confirms these two modes use a single Arena config.
        if (mode != "AKFFA" && mode != "PrimitiveFFA") return null;
        try
        {
            var plugin = plugins.Find(mode);
            var config = plugin?.GetType().GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(plugin);
            var arena = config?.GetType().GetField("Arena")?.GetValue(config);
            return arena?.GetType().GetField("Name")?.GetValue(arena) as string;
        }
        catch { return null; }
    }

    [ConsoleCommand("fiveheads.feedbackmode")]
    private void FeedbackMode(ConsoleSystem.Arg arg)
    {
        if (arg == null) return;
        if (arg.Connection != null) return;
        if (arg.Args == null || arg.Args.Length != 1 || !ulong.TryParse(arg.Args[0], out ulong id))
        {
            arg.ReplyWith("Usage: fiveheads.feedbackmode <SteamID64>");
            return;
        }
        string mode = null;
        string status = "unknown";
        int? online = null, modePlayers = null, ping = null;
        double? fps = null;
        string map = null;
        int? cellX = null, cellZ = null;
        try
        {
            var player = BasePlayer.activePlayerList.FirstOrDefault(p => p != null && p.IsConnected && (ulong)p.userID == id);
            var core = CoreLobby ?? plugins.Find("CoreLobby");
            if (core == null) status = "corelobby_not_loaded";
            else if (player == null) status = "player_not_online";
            else
            {
                // CoreLobby 2.3.85 exposes public methods, not registered hooks.
                var flags = BindingFlags.Public | BindingFlags.Instance;
                var getMode = core.GetType().GetMethod("GetPlayerGamemode", flags, null, new[] { typeof(ulong) }, null);
                var getAll = core.GetType().GetMethod("GetAllGamemodePlayers", flags, null, Type.EmptyTypes, null);
                var direct = getMode != null ? getMode.Invoke(core, new object[] { id }) as string : core.Call("GetPlayerGamemode", id) as string;
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    mode = direct;
                    status = getMode != null ? "reflection_direct" : "hook_direct";
                }
                else
                {
                    var all = (getAll != null ? getAll.Invoke(core, null) : core.Call("GetAllGamemodePlayers")) as IDictionary<ulong, string>;
                    if (all == null) status = "mode_api_unavailable";
                    else if (all.TryGetValue(id, out string tracked))
                    {
                        if (!string.IsNullOrWhiteSpace(tracked)) { mode = tracked; status = "tracked"; }
                        else status = "empty_mode";
                    }
                    else
                    {
                        var inMode = core.GetType().GetMethod("IsPlayerInGamemode", flags, null, new[] { typeof(ulong) }, null);
                        if (inMode != null && inMode.Invoke(core, new object[] { id }) is bool active && !active)
                        { mode = "Lobby"; status = "not_in_tracked_mode"; }
                        else status = "mode_not_tracked";
                    }
                }
            }
            if (player != null)
            {
                online = BasePlayer.activePlayerList.Count(p => p != null && p.IsConnected);
                if (!string.IsNullOrWhiteSpace(mode) && core != null)
                {
                    try
                    {
                        var count = core.GetType().GetMethod("GetGamemodePlayerCount", new[] { typeof(string) });
                        if (count?.Invoke(core, new object[] { mode }) is int n) modePlayers = n;
                    }
                    catch { }
                    map = ReadSingleArenaName(mode);
                }
                try
                {
                    var connection = player.net?.connection;
                    if (connection != null) ping = Network.Net.sv.GetAveragePing(connection);
                }
                catch { }
                try
                {
                    if (UnityEngine.Time.smoothDeltaTime > 0) fps = Math.Round(1.0 / UnityEngine.Time.smoothDeltaTime, 1);
                    var pos = player.transform.position;
                    cellX = (int)Math.Floor(pos.x / 25.0) * 25;
                    cellZ = (int)Math.Floor(pos.z / 25.0) * 25;
                }
                catch { }
            }
        }
        catch (Exception ex) { status = "lookup_error:" + ex.GetType().Name; mode = null; }
        long observed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = history.TryGetValue(id, out var list) ? list.Where(e => observed - e.at <= 600000).ToArray() : Array.Empty<GameEvent>();
        var result = JsonConvert.SerializeObject(new { steam_id = id.ToString(), mode, status, observed_at = observed, map, online, mode_players = modePlayers, ping_ms = ping, estimated_fps = fps, cell_x = cellX, cell_z = cellZ, events });
        arg.ReplyWith(result);
    }
}
