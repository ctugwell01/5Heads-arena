'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { buildPrisonEmbeds } = require('../prison-logs');

test('Owner logs include the reason, VIP logs hide it', function() {
  const time = '2026-10-11T14:00:00.000Z';
  const messages = buildPrisonEmbeds('Player123', '76561199205214362', 'HateSpeech', 2, time);
  const owner = JSON.stringify(messages.owner);
  const vip = JSON.stringify(messages.vip);
  assert.match(owner, /HateSpeech/);
  assert.match(owner, /#2/);
  assert.doesNotMatch(vip, /HateSpeech|Reason|#2/);
  assert.match(vip, /Player123/);
  assert.match(vip, /76561199205214362/);
  assert.match(vip, /2026-10-11T14:00:00.000Z/);
});

test('VIP notification never contains a custom prison reason', function() {
  const secret = 'PrivateSecretReason_98765';
  const messages = buildPrisonEmbeds('VIPPlayer', '76561199205214362', secret, null);
  assert.equal(JSON.stringify(messages.vip).includes(secret), false);
  assert.equal(JSON.stringify(messages.owner).includes(secret), true);
  assert.match(JSON.stringify(messages.owner), /N\/A/);
});

test('Player names are sanitized and empty offence counts stay accurate', function() {
  const messages = buildPrisonEmbeds('Name\nInjected', '76561199205214362', 'Spamming', 0);
  const player = messages.vip.fields.find(field => field.name === 'Player');
  const offence = messages.owner.fields.find(field => field.name === 'Offence');
  assert.equal(player.value, 'Name Injected');
  assert.equal(offence.value, '#0');
});

test('Both Discord feeds show the actual sentence duration', function() {
  const embeds = buildPrisonEmbeds('Tester', '76561199205214362', 'Spamming', 1, '2026-10-14T12:00:00Z', 1200);
  assert.equal(embeds.vip.fields.find(field => field.name === 'Sentence').value, '20 minutes');
  assert.equal(embeds.owner.fields.find(field => field.name === 'Sentence').value, '20 minutes');
  assert.equal(JSON.stringify(embeds.vip).includes('Spamming'), false);
});

test('Permanent punishments show the sentence without leaking a reason', function() {
  const embeds = buildPrisonEmbeds('Player', '76561199205214362', 'SomeSecretOffence', null, undefined, 0);
  assert.equal(embeds.vip.fields.find(field => field.name === 'Sentence').value, 'Permanent');
  assert.equal(JSON.stringify(embeds.vip).includes('SomeSecretOffence'), false);
});
