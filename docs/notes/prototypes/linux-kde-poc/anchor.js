// THROWAWAY compositor-placement experiment, restricted to this PoC's own two flyouts.
// The cursor is an activation-time stand-in for the icon anchor. Production needs panel-provided
// icon geometry or captured activation coordinates; this does not prove that integration contract.
function isProbe(window) {
  return window && String(window.resourceClass) === 'little-launcher-kde-poc' &&
    (window.caption === 'LL PoC Shortcuts' || window.caption === 'LL PoC Web');
}

/** Place the probe toward its clicked panel edge, respecting KWin's usable screen area. */
function place(window, reason) {
  if (!isProbe(window)) return;
  var point=workspace.cursorPos;
  var area=workspace.clientArea(KWin.PlacementArea, window);
  var screen=workspace.clientArea(KWin.ScreenArea, window);
  var g=window.frameGeometry;
  var edges=[point.y-screen.y,screen.y+screen.height-point.y,point.x-screen.x,screen.x+screen.width-point.x];
  var edge=edges.indexOf(Math.min.apply(Math,edges));
  var x=Math.max(area.x+8,Math.min(area.x+area.width-g.width-8,point.x-g.width/2));
  var y=Math.max(area.y+8,Math.min(area.y+area.height-g.height-8,point.y-g.height/2));
  if(edge===0) y=area.y+8;
  if(edge===1) y=area.y+area.height-g.height-8;
  if(edge===2) x=area.x+8;
  if(edge===3) x=area.x+area.width-g.width-8;
  window.frameGeometry={x:Math.round(x),y:Math.round(y),width:g.width,height:g.height};
  print('LL_ANCHOR '+JSON.stringify({reason:reason,caption:window.caption,edge:edge,cursor:{x:point.x,y:point.y},area:{x:area.x,y:area.y,width:area.width,height:area.height},requested:{x:x,y:y,width:g.width,height:g.height}}));
}
workspace.windowAdded.connect(function(window){place(window,'window-added');});
print('LL_ANCHOR_READY');
