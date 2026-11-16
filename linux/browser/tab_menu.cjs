'use strict';
const {validUrl} = require('./urls.cjs');
const {bookmarked} = require('./web_menu.cjs');
const {t} = require('./i18n.cjs');

/** Windows-reference tab commands bound to an individual tab, never the active-tab index. */
function tabTemplate(owner, tab, invoke) {
  if (!tab) return [{label: t('webTabs.reopenClosedTab'), enabled: !!owner.closedTabs.length, click: () => invoke('reopen')}];
  const url = tab.view?.webContents.getURL() || tab.url;
  const address = validUrl(url);
  const saved = bookmarked(owner.config.WebBookmarks || [], url);
  return [
    {label: t('web.reload'), enabled: !!tab.view, click: () => invoke('reload')},
    {label: t('webTabs.duplicate'), enabled: address, click: () => invoke('duplicate')},
    {type: 'separator'},
    {label: t('webTabs.copyAddress'), enabled: address, click: () => invoke('copy')},
    {label: t('web.openInBrowser'), enabled: address, click: () => invoke('external')},
    {label: t(saved ? 'web.removeFromBookmarks' : 'web.addToBookmarks'), enabled: address && owner.canEditBookmarks(),
      click: () => invoke('bookmark')},
    {type: 'separator'},
    {label: t('webTabs.closeTab'), click: () => invoke('close')},
    {label: t('webTabs.closeOtherTabs'), enabled: owner.tabs.length > 1, click: () => invoke('close-others')},
    {label: t('webTabs.reopenClosedTab'), enabled: !!owner.closedTabs.length, click: () => invoke('reopen')},
  ];
}
module.exports = {tabTemplate};
