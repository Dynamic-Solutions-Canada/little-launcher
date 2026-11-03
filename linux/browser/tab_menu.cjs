'use strict';
const {validUrl} = require('./urls.cjs');
const {bookmarked} = require('./web_menu.cjs');

/** Windows-reference tab commands bound to an individual tab, never the active-tab index. */
function tabTemplate(owner, tab, invoke) {
  if (!tab) return [{label: 'Reopen closed tab', enabled: !!owner.closedTabs.length, click: () => invoke('reopen')}];
  const url = tab.view?.webContents.getURL() || tab.url;
  const address = validUrl(url);
  const saved = bookmarked(owner.config.WebBookmarks || [], url);
  return [
    {label: 'Reload', enabled: !!tab.view, click: () => invoke('reload')},
    {label: 'Duplicate', enabled: address, click: () => invoke('duplicate')},
    {type: 'separator'},
    {label: 'Copy address', enabled: address, click: () => invoke('copy')},
    {label: 'Open in browser', enabled: address, click: () => invoke('external')},
    {label: saved ? 'Remove from bookmarks' : 'Add to bookmarks', enabled: address && owner.canEditBookmarks(),
      click: () => invoke('bookmark')},
    {type: 'separator'},
    {label: 'Close tab', click: () => invoke('close')},
    {label: 'Close other tabs', enabled: owner.tabs.length > 1, click: () => invoke('close-others')},
    {label: 'Reopen closed tab', enabled: !!owner.closedTabs.length, click: () => invoke('reopen')},
  ];
}
module.exports = {tabTemplate};
