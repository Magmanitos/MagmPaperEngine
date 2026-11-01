const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('wallpaper', {
  source: () => ipcRenderer.invoke('wallpaper:source'),
  onMouse: callback => {
    const listener = (_event, position) => callback(position);
    ipcRenderer.on('wallpaper:mouse', listener);
    return () => ipcRenderer.removeListener('wallpaper:mouse', listener);
  },
  result: error => ipcRenderer.send('wallpaper:result', error)
});
