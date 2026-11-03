'use strict';
const {validUrl} = require('./urls.cjs');
const zoomLevels = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200];
const anchorChoices = [['Near its tray icon',0], ['Top left',1], ['Top centre',2], ['Top right',3],
  ['Left',4], ['Centre',5], ['Right',6], ['Bottom left',7], ['Bottom centre',8], ['Bottom right',9]];
function stepZoom(percent, direction) {
  const current = Math.max(25, Math.min(400, Number.isInteger(percent) && percent > 0 ? percent : 100));
  const levels = [...new Set([...zoomLevels, current])].sort((a,b)=>a-b);
  return levels[Math.max(0, Math.min(levels.length-1, levels.indexOf(current) + direction))];
}
function bookmarked(items, address, parent = []) {
  for (const [index,item] of items.entries()) {
    const path = [...parent,index];
    if (item.IsFolder) {const found = bookmarked(item.Children || [],address,path);if(found)return found;}
    else if (item.Url === address) return path;
  }
}
function moreTemplate(owner) {
  const config=owner.config, contents=owner.view?.webContents;
  const toggle=(label,key,inverse=false)=>({label,type:'checkbox',checked:inverse?!config[key]:!!config[key],
    enabled:!owner.detached,click:()=>void owner.preference(key,!config[key])});
  const link=section=>({label:section+' settings…',click:()=>owner.settings(section)});
  const zoom=config.WebZoomPercent||100;
  const saved=bookmarked(config.WebBookmarks||[],contents?.getURL());
  const behavior=[toggle('Regular window','WebRegularWindow')];
  if(config.WebRegularWindow)behavior.push(toggle('Close when focus is lost','WebWindowAutoHide'));
  const pin=toggle(config.WebRegularWindow?'Keep window on top':'Keep flyout open','WebPinFlyout');
  if(config.WebRegularWindow && process.env.XDG_SESSION_TYPE==='wayland'){pin.enabled=false;pin.sublabel='Use the KDE window menu for window stacking';}
  behavior.push(pin);
  behavior.push(toggle('Fullscreen fills launcher','WebFullScreenInWindow'));
  const fit=toggle('Fit fullscreen to video','WebFitFullscreenToVideo');fit.enabled=!!config.WebFullScreenInWindow && !owner.detached;behavior.push(fit);
  behavior.push({type:'separator'},link('Behavior'));
  return [
    {label:saved?'Remove from bookmarks':'Add to bookmarks',enabled:owner.canEditBookmarks()&&validUrl(contents?.getURL()),
      click:()=>void owner.bookmarkRequest(saved?{type:'bookmark-action',action:'remove',path:saved,expected:structuredClone(config.WebBookmarks||[])}:
        {type:'bookmark-add',url:contents.getURL(),title:contents.getTitle().slice(0,256)})},
    {label:'Open in browser',enabled:validUrl(contents?.getURL()),click:()=>owner.action('external')},
    {type:'separator'},
    {label:'General',submenu:[link('General')]},
    {label:'Appearance',submenu:[toggle('Tab bar','WebAlwaysShowTabs'),toggle('Address bar','WebShowAddressBar'),
      toggle('Bookmarks bar','WebShowBookmarkBar'),{type:'separator'},link('Appearance')]},
    {label:'Size and position',submenu:[
      {label:`Zoom (${zoom}%)`,submenu:[{label:'Reset to 100%',enabled:zoom!==100,click:()=>void owner.preference('WebZoomPercent',100)},
        {type:'separator'},...[...new Set([...zoomLevels,zoom])].sort((a,b)=>a-b).map(level=>({label:`${level}%`,type:'radio',checked:zoom===level,
          enabled:!owner.detached,click:()=>void owner.preference('WebZoomPercent',level)}))]},
      {label:'Opens at',submenu:[...anchorChoices.map(([label,value])=>({label,type:'radio',checked:(config.WebAnchor||0)===value,
        enabled:!owner.detached,click:()=>void owner.preference('WebAnchor',value)})),
        ...(owner.presentation?.positionSupported || config.WebAnchor===10?[{label:'Where you last dragged it',type:'radio',checked:config.WebAnchor===10,enabled:!!owner.presentation?.positionSupported,click:()=>void owner.preference('WebAnchor',10)}]:[])]},
      {label:'Choose opening position…',enabled:!owner.detached && owner.canEditBookmarks(),click:()=>owner.action('position-picker')},
      toggle('Remember size changes','WebLockSize',true),{type:'separator'},link('Size and position')]},
    {label:'Behavior',submenu:behavior},
    {label:'Privacy and sign-ins',submenu:[link('Privacy and sign-ins')]},
    {type:'separator'},
    {label:'Advanced',submenu:[{label:'Developer tools',click:()=>contents?.openDevTools({mode:'detach'})},
      {label:'Reload, ignoring cache',click:()=>contents?.reloadIgnoringCache()},
      {type:'separator'},{label:'Turn on notifications',enabled:validUrl(contents?.getURL()),
        click:()=>void contents?.executeJavaScript('Notification.requestPermission()',true).catch(()=>owner.emit({type:'warning',message:'This site could not request notification permission.'}))}]},
    {label:'Keyboard shortcuts',click:()=>owner.showShortcuts()},
    {label:'Launcher settings…',click:()=>owner.settings()},
    {label:'App settings…',click:()=>{if(owner.anchored)owner.hide();owner.emit({type:'app-settings'});}},
  ];
}
module.exports={moreTemplate,zoomLevels,anchorChoices,bookmarked,stepZoom};
