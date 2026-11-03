'use strict';
const {bookmarkVersion, projectBookmarks, resolveBookmark} = require('./bookmarks.cjs');

/** Translate a bar drop through full saved paths; visible indexes never become saved indexes. */
function bookmarkDrop(config, value) {
  const items = config.WebBookmarks || [];
  if (!value || value.version !== bookmarkVersion(items) || (!Number.isInteger(value.source) && !validPath(value.source)) ||
      (!Number.isInteger(value.target) && !validPath(value.target)) || typeof value.after !== 'boolean' || typeof value.inside !== 'boolean') return null;
  const projected = projectBookmarks(items);
  const resolve=target=>Array.isArray(target) ? resolveBookmark(items,target)?.item && { ...resolveBookmark(items,target).item,sourcePath:target} : projected[target];
  const source=resolve(value.source),target=resolve(value.target);
  if (!source || (value.target !== -1 && !target)) return null;
  const parent = value.inside && target?.IsFolder ? target.sourcePath : target ? target.sourcePath.slice(0, -1) : [];
  if (source.IsFolder && parent.length >= source.sourcePath.length && source.sourcePath.every((part, i) => parent[i] === part)) return null;
  return {action:'reorder', path:source.sourcePath, expected:structuredClone(items), parent,
    before: value.inside && target?.IsFolder || !target ? null : target.sourcePath.at(-1) + (value.after ? 1 : 0)};
}
function validPath(path) {return Array.isArray(path) && path.length>0 && path.length<=32 && path.every(part=>Number.isInteger(part) && part>=0);}
module.exports = {bookmarkDrop};
