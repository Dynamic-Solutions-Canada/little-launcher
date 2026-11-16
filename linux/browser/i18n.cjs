'use strict';
/**
 * Interface text for the Electron main process and, through it, the trusted local chrome.
 *
 * The native Qt process resolves the language (linux/little_launcher/i18n.py) and passes the
 * catalog name and the catalog directory as `--launcher-language=` and `--launcher-catalogs=`.
 * This module never matches languages itself; it only reads the named catalog, falls back to
 * en-US, and fills placeholders and plural forms exactly as i18n.py does. See docs/localization.md.
 *
 * It must not require Electron: the menu modules that use it are tested under plain Node.
 */
const fs = require('node:fs');
const path = require('node:path');

const DEFAULT = 'en-US';
// The wording catalogs that exist (i18n.CATALOGS values). Anything else means English.
const CATALOGS = new Set(['en-US', 'es-ES', 'fr-FR', 'de-DE', 'zh-CN', 'pt-BR', 'ja-JP']);
// An area file is a few kilobytes; refuse anything that is clearly not one.
const MAX_FILE_BYTES = 1024 * 1024;
const MAX_FILES = 128;
const PLACEHOLDER = /\{([A-Za-z][A-Za-z0-9]*)\}/g;

let catalog = DEFAULT;
let directory = defaultDirectory();
const cache = new Map();

/** The bundled AppImage copy beside browser/, else the source checkout's shared catalogs. */
function defaultDirectory() {
  const bundled = path.join(__dirname, '..', 'localization');
  try {
    if (fs.statSync(bundled).isDirectory()) return bundled;
  } catch {
    // Not an AppImage layout; use the source tree.
  }
  return path.join(__dirname, '..', '..', 'Shared', 'Localization');
}

/**
 * Select the catalog chosen by the native process.
 * @param {{catalog?: string, directory?: string}} options An unknown catalog name means English;
 *   an empty directory keeps the default location. Cached catalogs are dropped on any change.
 */
function configure({catalog: name, directory: folder} = {}) {
  const nextCatalog = CATALOGS.has(name) ? name : DEFAULT;
  const nextDirectory = typeof folder === 'string' && folder && path.isAbsolute(folder) ? folder : directory;
  if (nextCatalog === catalog && nextDirectory === directory) return;
  catalog = nextCatalog;
  directory = nextDirectory;
  cache.clear();
}

/** Read `--launcher-language=` and `--launcher-catalogs=` from the main process arguments. */
function configureFromArguments(argv) {
  const value = prefix => argv.find(item => typeof item === 'string' && item.startsWith(prefix))?.slice(prefix.length);
  configure({catalog: value('--launcher-language='), directory: value('--launcher-catalogs=')});
}

/** The current catalog name, for diagnostics and tests. */
function current() {
  return catalog;
}

/**
 * Merge every area file of one catalog into a null-prototype map of key to text.
 * A missing, oversized or corrupt file is skipped, so its keys fall back to English.
 */
function read(name) {
  if (cache.has(name)) return cache.get(name);
  const merged = Object.create(null);
  let files = [];
  try {
    files = fs.readdirSync(path.join(directory, name)).filter(file => file.endsWith('.json')).sort().slice(0, MAX_FILES);
  } catch {
    // A catalog that is not installed reads as empty.
  }
  for (const file of files) {
    try {
      const location = path.join(directory, name, file);
      const stat = fs.lstatSync(location);
      if (!stat.isFile() || stat.size > MAX_FILE_BYTES) continue;
      const entries = JSON.parse(fs.readFileSync(location, 'utf8'));
      if (!entries || typeof entries !== 'object' || Array.isArray(entries)) continue;
      for (const [key, text] of Object.entries(entries)) {
        if (typeof text === 'string') merged[key] = text;
      }
    } catch {
      // Unreadable or invalid JSON: skip this area, as i18n.py does.
    }
  }
  cache.set(name, merged);
  return merged;
}

/** Replace each known `{name}` once; inserted values are never expanded again. */
function fill(template, values) {
  if (!values) return template;
  return template.replace(PLACEHOLDER, (match, name) =>
    Object.prototype.hasOwnProperty.call(values, name) ? String(values[name]) : match);
}

/**
 * The text for `key` in the current language, then English, then the key itself.
 * @param {string} key Catalog key such as `webTabs.closeTab`.
 * @param {object} [values] Named placeholder values; page and user content belongs here, never in keys.
 */
function t(key, values) {
  let text = read(catalog)[key];
  if (text === undefined) text = read(DEFAULT)[key];
  if (text === undefined) text = key;
  return fill(text, values);
}

/** Whether a count takes the "one" form; the same rule as i18n.py `_uses_one`. */
function usesOne(identity, count) {
  const language = identity.split('-')[0];
  if (language === 'fr' || language === 'pt') return count === 0 || count === 1;
  if (language === 'zh' || language === 'ja') return false;
  return count === 1;
}

/** Count-dependent text from `key.one` / `key.other`, with `{count}` supplied. */
function plural(key, count, values = {}) {
  const form = usesOne(catalog, count) ? '.one' : '.other';
  return t(key + form, {...values, count});
}

/**
 * A frozen key-to-text object for one trusted local page.
 * @param {Object<string, string|[string, object]>} entries Page name to catalog key, or to a
 *   [key, values] pair. Only these resolved strings reach the renderer, never the catalogs.
 */
function strings(entries) {
  const result = {};
  for (const [name, entry] of Object.entries(entries)) {
    result[name] = Array.isArray(entry) ? t(entry[0], entry[1]) : t(entry);
  }
  return Object.freeze(result);
}

module.exports = {configure, configureFromArguments, current, t, plural, strings, usesOne};
