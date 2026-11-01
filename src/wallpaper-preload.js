const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('wallpaper', {
  source: () => ipcRenderer.invoke('wallpaper:source'),
  result: error => ipcRenderer.send('wallpaper:result', error)
});
