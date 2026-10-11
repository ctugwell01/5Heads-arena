# 5Heads Arena Bot

Moderation bot for the 5Heads Rust Arena server.

## Railway setup

Required variables:

- `RCON_HOST` — Rust server hostname or IP only (no `ws://`)
- `RCON_PORT` — Rust WebRCON port
- `RCON_PASS` — Rust RCON password

Recommended variables:

- `ANTHROPIC_API_KEY` — enables AI moderation
- `DISCORD_WEBHOOK`
- `DISCORD_VOICE_WEBHOOK`
- `DISCORD_RECORDINGS_WEBHOOK`
- `DISCORD_VIP_LOG_WEBHOOK` — posts reason-hidden prison alerts to the VIP channel
- `DISCORD_OWNER_LOG_WEBHOOK` — posts prison alerts including the reason to the owner-only channel
- `DISCORD_BOT_TOKEN` — alternative to the two webhooks: a Discord bot token with Send Messages + Embed Links permissions in both channels
- `DISCORD_HISTORY_WEBHOOK`
- `DATA_DIR=/data`

The Rust server must have WebRCON enabled (normally `rcon.web 1`) and its RCON port must accept outbound connections from Railway.

## Discord prison logging: owner vs VIP

When the bot jails a player, **two separate alerts** are generated:

- **Owner logs** (Discord channel [1558850603533213867](https://discord.com/channels/1540790437508546700/1558850603533213867)): player, Steam ID/profile, UTC timestamp, reason, offence count if applicable.
- **VIP** (Discord channel [1558442485364293682](https://discord.com/channels/1540790437508546700/1558442485364293682)): player, Steam ID/profile, UTC timestamp **only**. No reason or offence detail is included.

You can configure delivery in Railway in **one** of two ways:

1. **Webhooks (recommended)**: in each Discord channel, open **Edit Channel → Integrations → Webhooks → New Webhook**, copy the URL, and set the matching Railway variable (`DISCORD_VIP_LOG_WEBHOOK` and `DISCORD_OWNER_LOG_WEBHOOK`). Keep webhook URLs secret. The webhook determines the destination channel.
2. **Discord bot token**: set `DISCORD_BOT_TOKEN` in Railway and invite the bot into the Discord server with **View Channel**, **Send Messages**, and **Embed Links** permission for the two channels. The code defaults to the channel IDs above; optionally override them via `DISCORD_VIP_CHANNEL_ID` and `DISCORD_OWNER_CHANNEL_ID`.

**Important:** Discord channel links or channel IDs are *not* webhooks and cannot send messages alone. A bot token or the two webhook URLs must be configured. Detailed owner messages deliberately **do not** fall back to `DISCORD_WEBHOOK` or the old `DISCORD_PRISON_LOG_WEBHOOK`, because those could expose reasons in an unintended channel.

Both channels receive alerts for **every** automatic prison event. The VIP channel is a redacted notification feed, not a list of VIP players. Existing in-game announcements and other voice-moderation channels are unchanged; they may still display the reason separately. Stored prison history retains the reason for moderator review.

## Manual jail actions from the in-game admin UI

**Important:** Discord logs created by `prisonPlayer()` only cover automatic punishments initiated by this Node bot. Manual imprisonment from an admin UI happens inside the Rust server plugin and does **not** invoke the Node bot automatically.

To log manual UI imprisonment:

1. Install [`server-plugin/PrisonLogBridge.cs`](server-plugin/PrisonLogBridge.cs) on the **Rust game server**, at `oxide/plugins/PrisonLogBridge.cs` (or the equivalent Carbon plugin directory). This cannot be installed by updating the Railway app.
2. The bridge listens for Rust plugin hook `OnPlayerJail(ulong playerId, double sentenceSeconds)`, used by some Jail plugins. **Not every prison UI provides this hook.**
3. If the UI plugin doesn't issue that hook, add the following call **after it successfully puts the player in prison**, using its own variable names and reason:

   ```csharp
   Interface.CallHook("On5HeadsPrisoned", target.UserIDString, target.displayName, reason);
   ```

   Use `"Not supplied by admin UI"` instead of `reason` when no reason is available. Some prison admin UIs have no reason entry at all.
4. Look for a `[5HEADS_PRISON]` line in the Rust server console and `[PRISON EVENT] UI event received` and `[DISCORD] OWNER/VIP prison log delivered` in Railway logs.

Both channels receive a message when a server-side bridge event is emitted; only Owner logs receive the reason. The event is saved to history with `source: "admin-ui"`. The Node bot suppresses duplicate notifications from its own auto-jail commands.

**Webhook connection test (without jailing anyone):** An authorised admin (one of the Steam IDs already allowed for `!history`) can enter `!testprisonlogs` in game chat. This sends clearly marked test messages to both Discord channels and logs the delivery result in Railway. It does not jail anyone or add prison history. A Discord HTTP success only confirms webhooks; it does not confirm that the UI is sending prison events.

## Persistence

Mount a Railway Volume at `/data`. The bot stores learned examples, blocklist additions, offence counts and prison history there. Runtime history is no longer committed back to GitHub, preventing moderation events from triggering new Railway deployments.

The legacy GitHub history file can still be loaded at startup when `GITHUB_TOKEN` is configured. Defaults:

- `GITHUB_REPO=ctugwell01/5Heads-arena`
- `GITHUB_PATH=prison_history_arena.json`

## Health endpoints

- `/health` — process health; always returns HTTP 200 while the bot process is alive
- `/ready` — returns HTTP 200 only when Rust RCON is connected

Both responses include the current RCON state and process uptime.

## Diagnostics

Useful runtime log messages:

- `[RCON] Connected to Rust RCON`
- `[RCON] Error ECONNREFUSED`
- `[RCON] Error ETIMEDOUT`
- `[RCON] Handshake rejected with HTTP ...`
- `[RCON] Disconnected. code=...`

The bot redacts the RCON password from connection logs.

## Local check

```bash
npm install
npm run check
```
