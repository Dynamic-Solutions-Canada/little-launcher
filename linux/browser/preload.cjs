'use strict';
const {contextBridge, ipcRenderer} = require('electron');
// This preload is used only by the packaged local toolbar, never by a remote view.
contextBridge.exposeInMainWorld('launcher', {
  action: (action, value) => ipcRenderer.send('toolbar-action', action, value),
  onFocusAddress: callback => ipcRenderer.on('focus-address', () => callback()),
  onFullscreen: callback => ipcRenderer.on('fullscreen-state', (_event,state)=>callback(state)),
  onBookmarkOverlay: callback => ipcRenderer.on('bookmark-overlay-state', (_event, state) => callback(state)),
  onBookmarkDrag: callback => ipcRenderer.on('bookmark-drag-state', (_event, state) => callback(state)),
  onState: callback => ipcRenderer.on('toolbar-state', (_event, state) => callback(state)),
});
