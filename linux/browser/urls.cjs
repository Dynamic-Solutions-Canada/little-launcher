'use strict';

/** Restrict remote navigation and bookmark rendering to ordinary web addresses. */
function validUrl(value) {
  try {
    return ['https:', 'http:'].includes(new URL(value).protocol);
  } catch {
    return false;
  }
}

module.exports = {validUrl};

/** Remote top-level pages stay HTTP(S); native WAR checks govern known extension iframe redirects. */
function allowedPageRedirect(value,isMainFrame,extensionForId) {
  if(validUrl(value))return true;
  if(isMainFrame!==false)return false;
  try {
    const url=new URL(value);
    return url.protocol==='chrome-extension:'&&!url.username&&!url.password&&/^[a-p]{32}$/.test(url.hostname)&&!!extensionForId(url.hostname);
  } catch {return false;}
}
module.exports.allowedPageRedirect=allowedPageRedirect;
