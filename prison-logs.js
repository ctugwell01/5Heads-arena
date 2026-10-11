'use strict';

// Build distinct Discord messages for owner-only and VIP-facing prison channels.
// Never reuse owner fields in the VIP message: the reason must not leak.
function buildPrisonEmbeds(username, userId, reason, offence, time) {
  const player = String(username || 'Unknown').replace(/[\r\n]/g, ' ').slice(0, 256);
  const steamId = String(userId || 'Unknown').replace(/[\r\n]/g, '').slice(0, 32);
  const recordedAt = time || new Date().toISOString();
  const profile = /^\d{17}$/.test(steamId)
    ? 'https://steamcommunity.com/profiles/' + steamId : 'Unknown';
  const publicFields = [
    { name: 'Player', value: player, inline: true },
    { name: 'Steam ID', value: steamId, inline: true },
    { name: 'Steam Profile', value: profile, inline: false },
    { name: 'Time (UTC)', value: recordedAt, inline: false }
  ];
  const vip = {
    title: 'Player Sent to Prison',
    color: 3447003,
    fields: publicFields,
    timestamp: recordedAt
  };
  const owner = {
    title: 'Player Sent to Prison — Owner Log',
    color: reason === 'HateSpeech' ? 15158332 : 15105570,
    fields: [
      ...publicFields,
      { name: 'Reason', value: String(reason || 'Unknown').slice(0, 1024), inline: true },
      { name: 'Offence', value: offence == null ? 'N/A' : '#' + offence, inline: true }
    ],
    timestamp: recordedAt
  };
  return { vip, owner };
}

module.exports = { buildPrisonEmbeds };
