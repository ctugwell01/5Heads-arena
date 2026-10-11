'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parseManualPrisonEvent } = require('../manual-prison-events');

test('parses manual UI event with reason from Oxide console prefix', function() {
  const event = parseManualPrisonEvent('[PrisonLogBridge] [5HEADS_PRISON] {"steamId":"76561199205214362","username":"Tester","reason":"Rule breaking"}');
  assert.deepEqual(event, { userId:'76561199205214362', username:'Tester', reason:'Rule breaking' });
});

test('supports UI without reason, showing honest owner-log placeholder', function() {
  assert.equal(parseManualPrisonEvent('[5HEADS_PRISON] {"steamId":"76561199205214362","username":"Tester"}').reason,'Not supplied by admin UI');
});

test('ignores player chat and spoofed events', function() {
  assert.equal(parseManualPrisonEvent('[CHAT] Test: [5HEADS_PRISON] {"steamId":"76561199205214362"}'),null);
  assert.equal(parseManualPrisonEvent('bad [5HEADS_PRISON] {"steamId":"76561199205214362"}'),null);
  assert.equal(parseManualPrisonEvent('[5HEADS_PRISON] {"steamId":"not-a-steam-id","username":"X"}'),null);
  assert.equal(parseManualPrisonEvent('[5HEADS_PRISON] no json'),null);
});
