'use strict';

const EVENT_MARKER = '[5HEADS_PRISON]';

// Only consume server plugin console messages. Player chat must be rejected by
// the caller before parsing to prevent forged punishment logs.
function parseManualPrisonEvent(line) {
  if (typeof line !== 'string' || line.length > 3000) return null;
  if (/\[(?:CHAT|VIPCHAT)\]/i.test(line)) return null;
  const match = line.match(/(?:^|\]\s*)\[5HEADS_PRISON\]\s*(\{[^\r\n]+\})\s*$/);
  if (!match) return null;
  let parsed;
  try { parsed = JSON.parse(match[1]); } catch (_) { return null; }
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return null;
  const steamId = String(parsed.steamId || '').trim();
  if (!/^7656119\d{10}$/.test(steamId)) return null;
  const username = String(parsed.username || '').replace(/[\r\n]/g, ' ').trim().slice(0, 128) || 'Unknown';
  const reason = String(parsed.reason || '').replace(/[\r\n]/g, ' ').trim().slice(0, 256)
    || 'Not supplied by admin UI';
  return { userId: steamId, username, reason };
}

module.exports = { parseManualPrisonEvent, EVENT_MARKER };
