'use strict';
const {validUrl} = require('./urls.cjs');
const {createHash} = require('node:crypto');
const {t} = require('./i18n.cjs');

/** Bind a rendered bar action to its complete saved tree, including undisplayed metadata. */
function bookmarkVersion(items) {
  return createHash('sha256').update(JSON.stringify(items)).digest('hex');
}

/** Keep source paths when omitting unusable addresses; never save this display projection. */
function projectBookmarks(items, parent = []) {
  return items.flatMap((item, index) => {
    const sourcePath = [...parent, index];
    if (item.IsFolder) {
      return [{Name: item.Name || t('webBookmarks.defaultFolderName'), IsFolder: true, IconsOnly: !!item.IconsOnly, sourcePath,
        Children: projectBookmarks(item.Children || [], sourcePath)}];
    }
    return validUrl(item.Url) ? [{Name: item.Name, Url: item.Url, IconsOnly: item.IconsOnly, sourcePath}] : [];
  });
}

/** Resolve a saved path without consulting filtered bar indexes. */
function resolveBookmark(items, sourcePath) {
  let collection = items;
  for (const [depth, index] of sourcePath.entries()) {
    if (!Number.isInteger(index) || !collection[index]) return null;
    const item = collection[index];
    if (depth === sourcePath.length - 1) return {item, collection, index};
    if (!item.IsFolder) return null;
    collection = item.Children || [];
  }
  return null;
}

/** Enumerate actual saved folder destinations with labels that disambiguate nesting. */
function folders(items, parent = [], prefix = '') {
  return items.flatMap((item, index) => {
    if (!item.IsFolder) return [];
    const sourcePath = [...parent, index];
    const label = prefix + (item.Name || t('webBookmarks.defaultFolderName'));
    return [{sourcePath, label}, ...folders(item.Children || [], sourcePath, label + ' / ')];
  });
}

/** Build desktop-owned edit actions; callbacks carry the original saved paths and snapshot. */
function bookmarkActions(items, sourcePath, invoke, iconsOnly = false, options = {}) {
  const selection = resolveBookmark(items, sourcePath);
  if (!selection) {
    return [
      {label: t('webBookmarks.addBookmarkMenu'), enabled: options.editable !== false, click: () => invoke('add', [])},
      {label: t('webBookmarks.addFromBrowserMenu'), enabled: options.editable !== false, click: () => invoke('import', sourcePath)},
      {label: t('webBookmarks.addFolderMenu'), enabled: options.editable !== false, click: () => invoke('add-folder', [])},
    ];
  }
  const {item, collection, index} = selection;
  const actions = [];
  if (item.IsFolder) {
    actions.push(
      {label: t('webBookmarks.addBookmarkMenu'), enabled: options.editable !== false, click: () => invoke('add', sourcePath)},
      {label: t('webBookmarks.addFromBrowserMenu'), enabled: options.editable !== false, click: () => invoke('import', sourcePath)},
      {label: t('webBookmarks.addFolderMenu'), enabled: options.editable !== false, click: () => invoke('add-folder', sourcePath)},
    );
    const contents = (item.Children || []).map((child, childIndex) => ({
      label: child.Name || child.Url || t('webBookmarks.defaultBookmarkName'),
      submenu: bookmarkActions(items, [...sourcePath, childIndex], invoke, iconsOnly, options),
    }));
    if (contents.length) actions.push({label: t('webBookmarks.editContents'), submenu: contents});
    actions.push({type: 'separator'});
  }
  if (!item.IsFolder) {
    const valid = validUrl(item.Url);
    actions.push(
      {label: t('common.open'), enabled: valid, click: () => invoke('open', sourcePath)},
      {label: t('webBookmarks.openInNewTab'), enabled: valid, click: () => invoke('open-tab', sourcePath)},
      {type: 'separator'},
      {label: t('webBookmarks.renameMenu'), enabled: options.editable !== false, click: () => invoke('rename', sourcePath)},
      {label: t('webBookmarks.editAddressMenu'), enabled: options.editable !== false, click: () => invoke('address', sourcePath)},
      {label: t('webBookmarks.copyAddress'), enabled: valid, click: () => invoke('copy', sourcePath)},
    );
  } else actions.push({label: t('webBookmarks.renameMenu'), enabled: options.editable !== false, click: () => invoke('edit', sourcePath)});
  actions.push(
    {label: t('webBookmarks.iconOnly'), type: 'checkbox', checked: iconsOnly || !!item.IconsOnly, enabled: !iconsOnly && options.editable !== false,
      click: menuItem => invoke('icons-only', sourcePath, {value: menuItem.checked})},
  );
  if (!item.IsFolder) actions.push(
    {label: t('webBookmarks.openInBrowser'), enabled: validUrl(item.Url), click: () => invoke('external', sourcePath)},
    {type: 'separator'},
    {label: t('webBookmarks.setAsHomePage'), enabled: options.editable !== false && validUrl(item.Url) && item.Url !== options.homeUrl,
      click: () => invoke('set-home', sourcePath)},
  );
  if (options.editable === false) return actions;
  actions.push(
    {label: t(sourcePath.length === 1 ? 'webBookmarks.moveLeft' : 'common.moveUp'), enabled: index > 0, click: () => invoke('step', sourcePath, {delta: -1})},
    {label: t(sourcePath.length === 1 ? 'webBookmarks.moveRight' : 'common.moveDown'), enabled: index < collection.length - 1,
      click: () => invoke('step', sourcePath, {delta: 1})},
  );
  const parent = sourcePath.slice(0, -1);
  if (parent.length) {
    actions.push({label: t('webBookmarks.moveToBar'), click: () => invoke('move', sourcePath, {parent: []})});
  }
  const destinations = folders(items).filter(folder => {
    const target = folder.sourcePath;
    const current = target.length === parent.length && target.every((value, i) => value === parent[i]);
    const descendant = item.IsFolder && target.length >= sourcePath.length &&
      sourcePath.every((value, i) => value === target[i]);
    return !current && !descendant;
  });
  if (destinations.length) actions.push({label: t('webBookmarks.moveToFolder'), submenu: destinations.map(folder => ({
    label: folder.label, click: () => invoke('move', sourcePath, {parent: folder.sourcePath}),
  }))});
  actions.push({type: 'separator'},
    {label: t(item.IsFolder ? 'webBookmarks.removeFolder' : 'common.remove'), click: () => invoke('remove', sourcePath)});
  return actions;
}

module.exports = {projectBookmarks, bookmarkActions, bookmarkVersion, resolveBookmark};
