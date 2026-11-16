'use strict';
const {validUrl} = require('./urls.cjs');
const {t} = require('./i18n.cjs');
const zoomLevels = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200];
// Saved WebAnchor values with their catalog keys; labels are resolved when a menu is built.
const anchorChoices = [['webPosition.anchor.tray',0], ['webPosition.anchor.topLeft',1], ['webPosition.anchor.topCenter',2],
  ['webPosition.anchor.topRight',3], ['webPosition.anchor.left',4], ['webPosition.anchor.center',5], ['webPosition.anchor.right',6],
  ['webPosition.anchor.bottomLeft',7], ['webPosition.anchor.bottomCenter',8], ['webPosition.anchor.bottomRight',9]];
// Settings section ids sent to the native controller, with their shared label keys.
const sections = {General:'launcherSettings.section.general', Appearance:'launcherSettings.section.appearance',
  'Size and position':'launcherSettings.section.sizeAndPosition', Behavior:'launcherSettings.section.behavior',
  'Privacy and sign-ins':'launcherSettings.section.privacyAndSignIns'};
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
  // The English section name is the wire id; only the label is translated.
  const link=section=>({label:t('webMenu.sectionSettingsLink',{section:t(sections[section])}),click:()=>owner.settings(section)});
  const zoom=config.WebZoomPercent||100;
  const saved=bookmarked(config.WebBookmarks||[],contents?.getURL());
  const behavior=[toggle(t('webMenu.regularWindow'),'WebRegularWindow')];
  if(config.WebRegularWindow)behavior.push(toggle(t('webMenu.closeOnFocusLoss'),'WebWindowAutoHide'));
  const pin=toggle(t(config.WebRegularWindow?'webMenu.alwaysOnTop':'webMenu.stayOpenOnFocusLoss'),'WebPinFlyout');
  if(config.WebRegularWindow && process.env.XDG_SESSION_TYPE==='wayland'){pin.enabled=false;pin.sublabel=t('linuxBrowser.kdeWindowStacking');}
  behavior.push(pin);
  behavior.push(toggle(t('webMenu.fullscreenFillsLauncher'),'WebFullScreenInWindow'));
  const fit=toggle(t('webMenu.fitFullscreenToVideo'),'WebFitFullscreenToVideo');fit.enabled=!!config.WebFullScreenInWindow && !owner.detached;behavior.push(fit);
  behavior.push({type:'separator'},link('Behavior'));
  return [
    {label:t(saved?'web.removeFromBookmarks':'web.addToBookmarks'),enabled:owner.canEditBookmarks()&&validUrl(contents?.getURL()),
      click:()=>void owner.bookmarkRequest(saved?{type:'bookmark-action',action:'remove',path:saved,expected:structuredClone(config.WebBookmarks||[])}:
        {type:'bookmark-add',url:contents.getURL(),title:contents.getTitle().slice(0,256)})},
    {label:t('web.openInBrowser'),enabled:validUrl(contents?.getURL()),click:()=>owner.action('external')},
    {type:'separator'},
    {label:t(sections.General),submenu:[link('General')]},
    {label:t(sections.Appearance),submenu:[toggle(t('webMenu.tabBar'),'WebAlwaysShowTabs'),toggle(t('webMenu.addressBar'),'WebShowAddressBar'),
      toggle(t('webMenu.bookmarksBar'),'WebShowBookmarkBar'),{type:'separator'},link('Appearance')]},
    {label:t(sections['Size and position']),submenu:[
      {label:t('webMenu.zoom',{percent:zoom}),submenu:[{label:t('webMenu.zoomReset'),enabled:zoom!==100,click:()=>void owner.preference('WebZoomPercent',100)},
        {type:'separator'},...[...new Set([...zoomLevels,zoom])].sort((a,b)=>a-b).map(level=>({label:t('webMenu.zoomLevel',{percent:level}),type:'radio',checked:zoom===level,
          enabled:!owner.detached,click:()=>void owner.preference('WebZoomPercent',level)}))]},
      {label:t('webMenu.opensAt'),submenu:[...anchorChoices.map(([key,value])=>({label:t(key),type:'radio',checked:(config.WebAnchor||0)===value,
        enabled:!owner.detached,click:()=>void owner.preference('WebAnchor',value)})),
        ...(owner.presentation?.positionSupported || config.WebAnchor===10?[{label:t('webPosition.anchor.lastPosition'),type:'radio',checked:config.WebAnchor===10,enabled:!!owner.presentation?.positionSupported,click:()=>void owner.preference('WebAnchor',10)}]:[])]},
      {label:t('webMenu.chooseOpeningPosition'),enabled:!owner.detached && owner.canEditBookmarks(),click:()=>owner.action('position-picker')},
      toggle(t('webMenu.rememberSize'),'WebLockSize',true),{type:'separator'},link('Size and position')]},
    {label:t(sections.Behavior),submenu:behavior},
    {label:t(sections['Privacy and sign-ins']),submenu:[link('Privacy and sign-ins')]},
    {type:'separator'},
    {label:t('webMenu.advanced'),submenu:[{label:t('webMenu.developerTools'),click:()=>contents?.openDevTools({mode:'detach'})},
      {label:t('webMenu.reloadIgnoringCache'),click:()=>contents?.reloadIgnoringCache()},
      {type:'separator'},{label:t('webMenu.turnOnNotifications'),enabled:validUrl(contents?.getURL()),
        click:()=>void contents?.executeJavaScript('Notification.requestPermission()',true).catch(()=>owner.emit({type:'warning',message:t('webMenu.notificationRequestFailed')}))}]},
    {label:t('webMenu.keyboardShortcuts'),click:()=>owner.showShortcuts()},
    {label:t('webMenu.launcherSettings'),click:()=>owner.settings()},
    {label:t('webMenu.appSettings'),click:()=>{if(owner.anchored)owner.hide();owner.emit({type:'app-settings'});}},
  ];
}
module.exports={moreTemplate,zoomLevels,anchorChoices,bookmarked,stepZoom};
