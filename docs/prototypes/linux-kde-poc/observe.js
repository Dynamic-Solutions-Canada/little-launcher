// Read-only compositor evidence for this private PoC's own windows.
function report(window, reason) {
  var g = window.frameGeometry;
  print('LL_GEOMETRY ' + JSON.stringify({reason:reason, caption:window.caption, resourceClass:String(window.resourceClass), geometry:{x:g.x,y:g.y,width:g.width,height:g.height}, cursor:{x:workspace.cursorPos.x,y:workspace.cursorPos.y}}));
}
function observe(window) {
  if (window.caption.indexOf('LL PoC') !== 0) return;
  report(window, 'window-added');
  window.frameGeometryChanged.connect(function(){report(window, 'geometry-changed');});
}
workspace.windowAdded.connect(observe);
var existing=workspace.windowList();
for (var i=0;i<existing.length;i++) observe(existing[i]);
print('LL_OBSERVER_READY');
