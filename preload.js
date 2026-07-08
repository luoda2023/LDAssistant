const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  openFileDialog: () => ipcRenderer.invoke('open-file-dialog'),
  readFileAsUrl: (filePath) => ipcRenderer.invoke('read-file-as-url', filePath),
  getFilePath: () => ipcRenderer.invoke('get-file-path'),
  setFilePath: (p) => ipcRenderer.invoke('set-file-path', p)
});
