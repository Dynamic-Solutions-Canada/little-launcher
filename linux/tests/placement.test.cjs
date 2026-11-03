'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

function place(point, area, screen = {x: 0, y: 0, width: 1920, height: 1080}, scale = 1, native = false, anchor = 0, saved = null) {
  const added = [];
  const signal = {connect() {}, disconnect() {}};
  const workspace = {windowAdded: {connect(fn) {added.push(fn);}}, windowActivated: signal,
    windowRemoved: signal, currentDesktop: {}, screens: [{geometry: screen, devicePixelRatio: scale}], screenAt: () => ({}), windowList: () => [],
    clientArea: kind => kind === 1 ? area : screen};
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../desktop/kwin/contents/code/main.js'), 'utf8'),
    {workspace, KWin: {PlacementArea: 1, ScreenArea: 2}});
  const window = {caption: `LittleLauncherFlyout:test:${point.x}:${point.y}${native ? ':native' : ''}${anchor ? ':at' + anchor : ''}${saved ? ':last:'+saved.join(':') : ''}`,
    resourceClass: 'io.github.RyanEwen.LittleLauncher', frameGeometry: {x: 0, y: 0, width: 300, height: 200},
    frameGeometryChanged: signal};
  added[0](window);
  return JSON.parse(JSON.stringify(window.frameGeometry));
}

test('tray overflow opens above its actual icon instead of snapping to the monitor corner', () => {
  assert.deepEqual(place({x: 1500, y: 700}, {x: 0, y: 0, width: 1920, height: 1032}),
    {x: 1350, y: 492, width: 300, height: 200});
});

test('horizontal panel corners stay on the horizontal edge; vertical reserved panels use their side', () => {
  assert.equal(place({x: 1910, y: 1048}, {x: 0, y: 0, width: 1920, height: 1032}).y, 824);
  assert.equal(place({x: 1910, y: 12}, {x: 0, y: 48, width: 1920, height: 1032}).y, 56);
  assert.deepEqual(place({x: 24, y: 700}, {x: 48, y: 0, width: 1872, height: 1080}),
    {x: 56, y: 600, width: 300, height: 200});
});

test('negative-coordinate outputs use their own work area', () => {
  assert.deepEqual(place({x: -900, y: 700}, {x: -1920, y: 0, width: 1920, height: 1032},
    {x: -1920, y: 0, width: 1920, height: 1080}), {x: -1050, y: 492, width: 300, height: 200});
});

test('150% Plasma native tray coordinates map to KWin logical geometry before anchoring', () => {
  const screen = {x: 0, y: 0, width: 2560, height: 1707};
  assert.deepEqual(place({x: 3132, y: 2541}, {x: 0, y: 0, width: 2560, height: 1659}, screen, 1.5, true),
    {x: 1938, y: 1451, width: 300, height: 200});
  assert.deepEqual(place({x: 1800, y: 900}, screen, screen, 1.5, true),
    {x: 1050, y: 392, width: 300, height: 200});
});

test('native-coordinate flyout titles retain focus-dismissal, pin and owned-dialog semantics', () => {
  for (const resourceClass of ['io.github.RyanEwen.LittleLauncher.Web', 'io.github.RyanEwen.LittleLauncher'])
  for (const [suffix, closes] of [[':native', 1], [':native:pinned', 0], [':native:owned', 0]]) {
    const activated = [];
    let closed = 0;
    const window = {resourceClass, caption: 'LittleLauncherFlyout:test:3132:2541' + suffix,
      closeWindow() {closed++;}};
    const workspace = {windowAdded: {connect() {}}, windowActivated: {connect(fn) {activated.push(fn);}},
      windowList: () => [window]};
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../desktop/kwin/contents/code/main.js'), 'utf8'), {workspace});
    for (const callback of activated) callback({resourceClass: 'foreign', caption: 'Foreign'});
    assert.equal(closed, closes, suffix);
  }
});


test('fixed anchors use the activated output work area and retain tray default', () => {
  const area={x:0,y:40,width:1280,height:720};
  assert.deepEqual(place({x:1100,y:750},area,{x:0,y:0,width:1280,height:800},1,false,1),
    {x:8,y:48,width:300,height:200});
  assert.deepEqual(place({x:1100,y:750},area,{x:0,y:0,width:1280,height:800},1,false,5),
    {x:490,y:300,width:300,height:200});
  assert.deepEqual(place({x:1100,y:750},area,{x:0,y:0,width:1280,height:800},1,false,9),
    {x:972,y:552,width:300,height:200});
});


test('device-local saved positions override tray placement and clamp to the current usable output',()=>{
  const area={x:0,y:0,width:1920,height:1032};
  assert.deepEqual(place({x:1800,y:1040},area,undefined,1,false,0,[220,180]),{x:220,y:180,width:300,height:200});
  assert.deepEqual(place({x:1800,y:1040},area,undefined,1,false,0,[5000,-5000]),{x:1612,y:8,width:300,height:200});
});
