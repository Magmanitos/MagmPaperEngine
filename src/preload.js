const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('magm', {
  list: () => ipcRenderer.invoke('library:list'),
  save: item => ipcRenderer.invoke('library:save', item),
  remove: id => ipcRenderer.invoke('library:remove', id),
  apply: id => ipcRenderer.invoke('library:apply', id),
  stop: () => ipcRenderer.invoke('library:stop'),
  onStatus: callback => ipcRenderer.on('status', (_, value) => callback(value))
});
