const {contextBridge, ipcRenderer} = require('electron');
contextBridge.exposeInMainWorld('probe', {file: () => ipcRenderer.invoke('file-dialog'), hide: () => ipcRenderer.send('hide'), quit: () => ipcRenderer.send('quit')});
