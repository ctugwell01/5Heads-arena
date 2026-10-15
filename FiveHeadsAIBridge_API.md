# FiveHeads AI Bridge v1 Contract

`FiveHeadsAIBridge.cs` is an outbound-only Carbon bridge. It never exposes RCON, a shell, arbitrary console execution, or arbitrary C# execution.

## Carbon -> Railway endpoints

Base path:

`/v1/servers/{serverId}`

Endpoints:

- `POST /heartbeat`
- `POST /events`
- `GET /commands?limit=10`
- `POST /command-results`

## HMAC request authentication

Every Carbon request contains:

- `X-5H-Server-ID`
- `X-5H-Timestamp` (Unix seconds)
- `X-5H-Nonce`
- `X-5H-Body-SHA256`
- `X-5H-Signature`
- `X-5H-Protocol-Version`

Canonical string:

METHOD
PATH_AND_QUERY
TIMESTAMP
NONCE
BODY_SHA256

Signature:

`hex(HMAC-SHA256(serverSecret, canonicalString))`

Railway should:

1. Look up the secret for `X-5H-Server-ID`.
2. Reject timestamps outside the allowed window (recommended +/- 60 seconds).
3. Reject a nonce that has already been used inside that window.
4. Recompute `SHA256(rawRequestBody)` and compare it to `X-5H-Body-SHA256`.
5. Recompute the HMAC signature and use a constant-time comparison.

For GET requests the body is the empty string.

## Heartbeat

Carbon sends approximately every 10 seconds.

Contains:

- server online/max/lobby population
- process uptime
- bridge/CoreLobby versions
- per-gamemode player counts
- loaded/offline gamemode plugin status
- missing critical plugins
- local telemetry/result queue depths

## Events

Events are queued and sent in batches of up to 20. Event IDs are globally unique and Railway should de-duplicate by `event_id`.

Current event types include:

- `server_started`
- `player_connected`
- `player_disconnected`
- `gamemode_joined`
- `gamemode_left`
- `plugin_loaded`
- `plugin_unloaded`
- `announcement_sent`
- `promotion_shown`
- `event_started`
- `event_stopped`
- `ai_question` — private read-only player question for the Railway AI assistant

## Command poll response

Railway returns:

```json
{
  "protocol_version": 1,
  "commands": [
    {
      "command_id": "cmd_...",
      "action": "send_lobby_popup",
      "issued_at": "2026-10-15T18:30:00Z",
      "expires_at": "2026-10-15T18:30:30Z",
      "requested_by": {
        "type": "ai",
        "id": "5heads-director"
      },
      "reason": "Promote an event to lobby players.",
      "arguments": {
        "title": "GUN GAME EVENT",
        "message": "JOIN NOW",
        "subtext": "30 minute event",
        "duration_seconds": 10
      }
    }
  ]
}
```

Commands without a valid `expires_at` are rejected.

## Compiled Carbon allowlist

The v1 plugin has handlers only for:

- `send_lobby_announcement`
- `send_lobby_popup`
- `send_player_message`
- `get_status_snapshot`
- `start_event`
- `stop_event`

The config has a second `Allowed actions` list. An action must exist in BOTH lists.

There is deliberately no:

- raw RCON
- raw console command
- shell
- arbitrary plugin method call
- wipe
- ban
- permission grant
- filesystem write
- arbitrary plugin reload

## Lobby-only rule

`send_lobby_announcement` and `send_lobby_popup` ask CoreLobby for each player's current gamemode. If CoreLobby is unavailable, they fail closed and show nothing.

The popup is also destroyed immediately when CoreLobby reports that the player entered a gamemode.

## Approved events

`start_event` and `stop_event` require an entry in the Carbon config under `Approved AI events`.

Example:

```json
"Approved AI events": {
  "gungame_hour": {
    "PluginName": "GunGame",
    "MaxDurationMinutes": 60
  }
}
```

That plugin must explicitly opt in by implementing:

- `AI_StartEvent(int durationMinutes)`
- `AI_StopEvent()`

No configured event = no event execution.

## Command results

Carbon POSTs a final status to `/command-results`:

- `succeeded`
- `failed`
- `rejected`
- `expired`
- `duplicate`

Processed command IDs are persisted locally so a Railway retry cannot execute the same side effect twice.

## Safe failure behavior

If Railway, Discord, the database, or the AI provider is unavailable, Rust/CoreLobby/gamemodes continue normally.

If authentication or action authorization cannot be verified, the AI action does not execute.