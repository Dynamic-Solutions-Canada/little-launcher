'use strict';
const {validUrl} = require('./urls.cjs');
const {createHash} = require('node:crypto');

/** Bind a rendered bar action to its complete saved tree, including undisplayed metadata. */
function bookmarkVersion(items) {
  return createHash('sha256').update(JSON.stringify(items)).digest('hex');
}

/** Keep source paths when omitting unusable addresses; never save this display projection. */
function projectBookmarks(items, parent = []) {
  return items.flatMap((item, index) => {
    const sourcePath = [...parent, index];
    if (item.IsFolder) {
      return [{Name: item.Name || 'Folder', IsFolder: true, IconsOnly: !!item.IconsOnly, sourcePath,
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
    const label = prefix + (item.Name || 'Folder');
    return [{sourcePath, label}, ...folders(item.Children || [], sourcePath, label + ' / ')];
  });
}

/** Build desktop-owned edit actions; callbacks carry the original saved paths and snapshot. */
function bookmarkActions(items, sourcePath, invoke, iconsOnly = false, options = {}) {
  const selection = resolveBookmark(items, sourcePath);
  if (!selection) {
    return [
      {label: 'Add bookmark…', enabled: options.editable !== false, click: () => invoke('add', [])},
      {label: 'Add from browser…', enabled: options.editable !== false, click: () => invoke('import', sourcePath)},
      {label: 'Add folder…', enabled: options.editable !== false, click: () => invoke('add-folder', [])},
    ];
  }
  const {item, collection, index} = selection;
  const actions = [];
  if (item.IsFolder) {
    actions.push(
      {label: 'Add bookmark…', enabled: options.editable !== false, click: () => invoke('add', sourcePath)},
      {label: 'Add from browser…', enabled: options.editable !== false, click: () => invoke('import', sourcePath)},
      {label: 'Add folder…', enabled: options.editable !== false, click: () => invoke('add-folder', sourcePath)},
    );
    const contents = (item.Children || []).map((child, childIndex) => ({
      label: child.Name || child.Url || 'Bookmark',
      submenu: bookmarkActions(items, [...sourcePath, childIndex], invoke, iconsOnly, options),
    }));
    if (contents.length) actions.push({label: 'Edit contents', submenu: contents});
    actions.push({type: 'separator'});
  }
  if (!item.IsFolder) {
    const valid = validUrl(item.Url);
    actions.push(
      {label: 'Open', enabled: valid, click: () => invoke('open', sourcePath)},
      {label: 'Open in new tab', enabled: valid, click: () => invoke('open-tab', sourcePath)},
      {type: 'separator'},
      {label: 'Rename…', enabled: options.editable !== false, click: () => invoke('rename', sourcePath)},
      {label: 'Edit address…', enabled: options.editable !== false, click: () => invoke('address', sourcePath)},
      {label: 'Copy address', enabled: valid, click: () => invoke('copy', sourcePath)},
    );
  } else actions.push({label: 'Rename…', enabled: options.editable !== false, click: () => invoke('edit', sourcePath)});
  actions.push(
    {label: 'Icon only', type: 'checkbox', checked: iconsOnly || !!item.IconsOnly, enabled: !iconsOnly && options.editable !== false,
      click: menuItem => invoke('icons-only', sourcePath, {value: menuItem.checked})},
  );
  if (!item.IsFolder) actions.push(
    {label: 'Open in browser', enabled: validUrl(item.Url), click: () => invoke('external', sourcePath)},
    {type: 'separator'},
    {label: 'Set as home page', enabled: options.editable !== false && validUrl(item.Url) && item.Url !== options.homeUrl,
      click: () => invoke('set-home', sourcePath)},
  );
  if (options.editable === false) return actions;
  actions.push(
    {label: sourcePath.length === 1 ? 'Move left' : 'Move up', enabled: index > 0, click: () => invoke('step', sourcePath, {delta: -1})},
    {label: sourcePath.length === 1 ? 'Move right' : 'Move down', enabled: index < collection.length - 1,
      click: () => invoke('step', sourcePath, {delta: 1})},
  );
  const parent = sourcePath.slice(0, -1);
  if (parent.length) {
    actions.push({label: 'Move to bookmark bar', click: () => invoke('move', sourcePath, {parent: []})});
  }
  const destinations = folders(items).filter(folder => {
    const target = folder.sourcePath;
    const current = target.length === parent.length && target.every((value, i) => value === parent[i]);
    const descendant = item.IsFolder && target.length >= sourcePath.length &&
      sourcePath.every((value, i) => value === target[i]);
    return !current && !descendant;
  });
  if (destinations.length) actions.push({label: 'Move to folder', submenu: destinations.map(folder => ({
    label: folder.label, click: () => invoke('move', sourcePath, {parent: folder.sourcePath}),
  }))});
  actions.push({type: 'separator'},
    {label: item.IsFolder ? 'Remove folder' : 'Remove', click: () => invoke('remove', sourcePath)});
  return actions;
}

module.exports = {projectBookmarks, bookmarkActions, bookmarkVersion, resolveBookmark};
