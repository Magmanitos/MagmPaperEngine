const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('magm', {
  openFolder: () => ipcRenderer.invoke('library:open-folder'),
  list: () => ipcRenderer.invoke('library:list'),
  save: item => ipcRenderer.invoke('library:save', item),
  remove: id => ipcRenderer.invoke('library:remove', id),
  apply: id => ipcRenderer.invoke('library:apply', id),
  stop: () => ipcRenderer.invoke('library:stop'),
  startup: () => ipcRenderer.invoke('startup:get'),
  setStartup: enabled => ipcRenderer.invoke('startup:set', enabled),
  setMouseEnabled: enabled => ipcRenderer.invoke('mouse:set', enabled),
  setSettings: patch => ipcRenderer.invoke('settings:set', patch),
  onPlayback: callback => ipcRenderer.on('playback:state', (_event, state) => callback(state)),
  onOpenSettings: callback => ipcRenderer.on('open-settings', () => callback()),
  onStatus: callback => ipcRenderer.on('status', (_, value) => callback(value))
});
