const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('wallpaper', {
  source: () => ipcRenderer.invoke('wallpaper:source'),
  onMouseEnabled: callback => {
    const listener = (_event, enabled) => callback(enabled);
    ipcRenderer.on('wallpaper:mouse-enabled', listener);
    return () => ipcRenderer.removeListener('wallpaper:mouse-enabled', listener);
  },
  onMouse: callback => {
    const listener = (_event, position) => callback(position);
    ipcRenderer.on('wallpaper:mouse', listener);
    return () => ipcRenderer.removeListener('wallpaper:mouse', listener);
  },
  result: error => ipcRenderer.send('wallpaper:result', error)
});
