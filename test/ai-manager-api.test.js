const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { createAiManagerApi } = require('../ai-manager-api');

function sign(method, target, body, serverId, secret, nonce) {
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const bodyHash = crypto.createHash('sha256').update(body).digest('hex');
  const canonical = [method, target, timestamp, nonce, bodyHash].join('\n');
  return {
    'content-type': 'application/json',
    'x-5h-server-id': serverId,
    'x-5h-timestamp': timestamp,
    'x-5h-nonce': nonce,
    'x-5h-body-sha256': bodyHash,
    'x-5h-signature': crypto.createHmac('sha256', secret).update(canonical).digest('hex'),
    'x-5h-protocol-version': '1'
  };
}

async function setup() {
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), '5heads-ai-'));
  const serverId = '5heads-arena-main';
  const secret = 's'.repeat(64);
  const adminToken = 'a'.repeat(48);
  const api = createAiManagerApi({ dataDir, serverId, bridgeSecret: secret, adminToken });
  const server = http.createServer(async (req, res) => {
    if (!await api.handle(req, res)) {
      res.statusCode = 404;
      res.end('Not found');
    }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { server, serverId, secret, adminToken, base: 'http://127.0.0.1:' + server.address().port };
}

async function bridgeFetch(ctx, method, target, object, nonce = crypto.randomUUID()) {
  const body = object == null ? '' : JSON.stringify(object);
  return fetch(ctx.base + target, {
    method,
    headers: sign(method, target, body, ctx.serverId, ctx.secret, nonce),
    body: method === 'GET' ? undefined : body
  });
}

test('signed heartbeat succeeds and replay is rejected', async t => {
  const ctx = await setup();
  t.after(() => ctx.server.close());
  const target = '/v1/servers/' + ctx.serverId + '/heartbeat';
  const heartbeat = {
    protocol_version: 1,
    server_id: ctx.serverId,
    instance_id: 'test-instance',
    server: { name: '5Heads', online: 5, max_players: 100, lobby_players: 2, uptime_seconds: 30 },
    gamemodes: [],
    health: { bridge_ok: true, corelobby_ok: true }
  };
  const nonce = '12345678-1234-1234-1234-123456789abc';
  assert.equal((await bridgeFetch(ctx, 'POST', target, heartbeat, nonce)).status, 200);
  const replay = await bridgeFetch(ctx, 'POST', target, heartbeat, nonce);
  assert.equal(replay.status, 409);
  assert.equal((await replay.json()).error.code, 'REPLAY_DETECTED');
});

test('admin queue -> Carbon poll -> result round trip', async t => {
  const ctx = await setup();
  t.after(() => ctx.server.close());
  const queuedResponse = await fetch(ctx.base + '/v1/admin/commands', {
    method: 'POST',
    headers: { authorization: 'Bearer ' + ctx.adminToken, 'content-type': 'application/json' },
    body: JSON.stringify({
      action: 'send_lobby_popup',
      reason: 'test',
      arguments: { title: '5HEADS', message: 'TEST', subtext: 'Bridge test', duration_seconds: 8 }
    })
  });
  assert.equal(queuedResponse.status, 201);
  const queued = await queuedResponse.json();
  const pollTarget = '/v1/servers/' + ctx.serverId + '/commands?limit=10';
  const poll = await bridgeFetch(ctx, 'GET', pollTarget, null);
  const payload = await poll.json();
  assert.equal(payload.commands[0].command_id, queued.command.command_id);

  const resultTarget = '/v1/servers/' + ctx.serverId + '/command-results';
  const result = await bridgeFetch(ctx, 'POST', resultTarget, {
    protocol_version: 1,
    command_id: queued.command.command_id,
    server_id: ctx.serverId,
    status: 'succeeded',
    finished_at: new Date().toISOString(),
    result: { players_reached: 3 }
  });
  assert.equal(result.status, 200);
});

test('raw RCON is rejected by Railway allowlist', async t => {
  const ctx = await setup();
  t.after(() => ctx.server.close());
  const response = await fetch(ctx.base + '/v1/admin/commands', {
    method: 'POST',
    headers: { authorization: 'Bearer ' + ctx.adminToken, 'content-type': 'application/json' },
    body: JSON.stringify({ action: 'raw_rcon', arguments: { command: 'quit' } })
  });
  assert.equal(response.status, 400);
  assert.equal((await response.json()).error.code, 'ACTION_NOT_ALLOWED');
});
