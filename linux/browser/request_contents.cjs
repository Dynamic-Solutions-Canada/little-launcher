'use strict';
/** WebRequest metadata may have no WebContents (for example a service-worker fetch). */
function requestContents(details, fromId) {
  const supplied = details.webContents;
  if (supplied && typeof supplied.isDestroyed === 'function' && !supplied.isDestroyed()) return supplied;
  const id = details.webContentsId;
  if (!Number.isInteger(id) || id <= 0 || id > 0x7fffffff) return null;
  try {
    const contents = fromId(id);
    return contents && !contents.isDestroyed() ? contents : null;
  } catch {
    // A requesting renderer can disappear before its network callback is delivered.
    return null;
  }
}
module.exports = {requestContents};
