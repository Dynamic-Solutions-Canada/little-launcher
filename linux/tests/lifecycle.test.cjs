'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const {EventEmitter} = require('node:events');
const {PageLifecycle} = require('../browser/lifecycle.cjs');

/** Deterministic engine boundary: inspect actual lifecycle commands and emitted state. */
function fixture(policy = 0) {
  const contents = new EventEmitter();
  const commands = [];
  contents.isDestroyed = () => false;
  contents.debugger = {isAttached: () => true, sendCommand: async (name, args) => commands.push([name, args])};
  const lifecycle = new PageLifecycle(contents, () => {}, policy, 1);
  return {contents, commands, lifecycle};
}

test('hidden idle pages freeze and active downloads resume them', async () => {
  const {commands, lifecycle} = fixture();
  await lifecycle.visibility(true);
  assert.equal(lifecycle.frozen, true);
  lifecycle.downloads.add('transfer');
  await lifecycle.update();
  assert.deepEqual(commands.map(command => command[1].state), ['frozen', 'active']);
  lifecycle.lastHidden = Date.now() - 120000;
  assert.equal(lifecycle.canUnload(), false);
  lifecycle.downloads.clear();
  assert.equal(lifecycle.canUnload(), true);
});

test('capture grants protect silent calls until navigation', async () => {
  const {contents, lifecycle} = fixture();
  lifecycle.capturePermission = true;
  await lifecycle.visibility(true);
  lifecycle.lastHidden = Date.now() - 120000;
  assert.equal(lifecycle.frozen, false);
  assert.equal(lifecycle.canUnload(), false);
  contents.emit('did-navigate');
  await lifecycle.update();
  assert.equal(lifecycle.busy, false);
  assert.equal(lifecycle.frozen, true);
});

test('keep-running starts and remains active while hidden', async () => {
  const {commands, lifecycle} = fixture(2);
  await lifecycle.visibility(true);
  lifecycle.lastHidden = Date.now() - 120000;
  assert.equal(lifecycle.canUnload(), false);
  assert.equal(commands.length, 0);
});

test('Electron 45 audio event protects a hidden audio-only page', async () => {
  const {contents, lifecycle} = fixture();
  await lifecycle.visibility(true);
  contents.emit('audio-state-changed', {audible: true});
  await lifecycle.update();
  assert.equal(lifecycle.busy, true);
  assert.equal(lifecycle.frozen, false);
  contents.emit('audio-state-changed', {audible: false});
  await lifecycle.update();
  assert.equal(lifecycle.busy, false);
  assert.equal(lifecycle.frozen, true);
});

test('a delayed freeze response cannot leave a reopened page frozen', async () => {
  const {contents, lifecycle} = fixture();
  const states = [];
  let release;
  contents.debugger.sendCommand = (_command, value) => {
    states.push(value.state);
    return value.state === 'frozen' ? new Promise(resolve => {release = resolve;}) : Promise.resolve();
  };
  const hiding = lifecycle.visibility(true);
  await Promise.resolve();
  const showing = lifecycle.visibility(false);
  release();
  await Promise.all([hiding, showing]);
  assert.deepEqual(states, ['frozen', 'active']);
  assert.equal(lifecycle.frozen, false);
});


test('hidden navigation finishes loading before it can freeze or idle-unload', async () => {
  const {contents,lifecycle}=fixture();
  contents.emit('did-start-loading');
  await lifecycle.visibility(true);
  lifecycle.lastHidden=Date.now()-600000;
  assert.equal(lifecycle.frozen,false);
  assert.equal(lifecycle.canUnload(),false);
  contents.emit('did-navigate');
  await lifecycle.update();
  assert.equal(lifecycle.frozen,false);
  contents.emit('did-stop-loading');
  await lifecycle.update();
  assert.equal(lifecycle.frozen,true);
});
