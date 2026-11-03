'use strict';
const {nativeImage} = require('electron');
const path = require('node:path');

/** Only the native host's bounded raster presentation crosses the icon boundary. */
function windowIcon(encoded) {
  if (typeof encoded === 'string' && encoded.length <= 128 * 1024 && /^[A-Za-z0-9+/]+={0,2}$/.test(encoded)) {
    const bytes = Buffer.from(encoded, 'base64');
    if (bytes.length >= 33 && bytes.subarray(0, 8).equals(Buffer.from('89504e470d0a1a0a', 'hex')) &&
        bytes.toString('ascii', 12, 16) === 'IHDR' &&
        bytes.readUInt32BE(16) > 0 && bytes.readUInt32BE(16) <= 128 &&
        bytes.readUInt32BE(20) > 0 && bytes.readUInt32BE(20) <= 128) {
      const icon = nativeImage.createFromBuffer(bytes);
      if (!icon.isEmpty()) return icon;
    }
  }
  return nativeImage.createFromPath(path.join(__dirname, '../resources/little-launcher.png'));
}
module.exports = {windowIcon};
