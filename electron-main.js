/**
 * Electron 主进程 - 标准查询桌面版
 * 功能：全屏文件预览 + 右下角 AI 浮动对话框
 * 文件预览引擎：@file-viewer/web-full (Apache 2.0)
 */
const { app, BrowserWindow, Tray, Menu, ipcMain, dialog, shell, screen, nativeImage } = require('electron');
const path = require('path');
const fs = require('fs');

let mainWindow = null;
let tray = null;

// ======= 创建主窗口 =======
function createMainWindow() {
  const { width: screenWidth, height: screenHeight } = screen.getPrimaryDisplay().workAreaSize;

  mainWindow = new BrowserWindow({
    width: screenWidth,
    height: screenHeight,
    show: false,
    icon: path.join(__dirname, 'public', 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: false
    }
  });

  mainWindow.loadFile(path.join(__dirname, 'public', 'index.html'));

  // 窗口准备好后显示（分批加载: 先显示界面壳，再加载内容）
  mainWindow.once('ready-to-show', () => {
    mainWindow.show();
    mainWindow.setAlwaysOnTop(false);
  });

  // 点击托盘时取消置顶
  mainWindow.on('hide', () => {
    if (tray && !tray.isDestroyed()) {
      mainWindow.setSkipTaskbar(true);
    }
  });
  mainWindow.on('show', () => {
    mainWindow.setSkipTaskbar(false);
  });
  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

// ======= 托盘 =======
function createTray() {
  try {
    const iconPath = path.join(__dirname, 'public', 'tray-icon.png');
    const icon = fs.existsSync(iconPath) ? nativeImage.createFromPath(iconPath) : nativeImage.createEmpty();
    const resizedIcon = icon.isEmpty() ? nativeImage.createFromBuffer(Buffer.alloc(32*32*4, 0)) : icon.resize({width:16,height:16});
    tray = new Tray(resizedIcon);
    tray.setToolTip('标准查询 - 工程助手');
    const contextMenu = Menu.buildFromTemplate([
      { label: '显示主窗口', click: () => { if(mainWindow) mainWindow.show(); else createMainWindow(); } },
      { label: '退出', click: () => { app.quit(); } }
    ]);
    tray.setContextMenu(contextMenu);
  } catch(e) {
    console.error('创建托盘失败:', e);
  }
}

// ======= IPC: 打开文件对话框 =======
ipcMain.handle('open-file-dialog', async (event, options) => {
  const result = await dialog.showOpenDialog(mainWindow, {
    title: '打开文件',
    properties: ['openFile', 'multiSelections'],
    filters: [
      { name: '所有支持的文件', extensions: [
        'pdf','docx','doc','xlsx','xls','ppt','pptx',
        'dwg','dxf','dgn','dwf',
        'txt','csv','json','xml',
        'png','jpg','jpeg','bmp','gif','tif','tiff','webp','svg',
        'html','htm','xhtml','mht',
        'zip','rar','7z','tar','gz',
        'ofd','typst','md','rst',
        'msg','eml','epub','xmind','vsdx'
      ] },
      { name: 'CAD 文件', extensions: ['dwg','dxf','dgn'] },
      { name: 'Office 文件', extensions: ['pdf','docx','doc','xlsx','xls','ppt','pptx'] },
      { name: '全部文件', extensions: ['*'] }
    ]
  });
  return result;
});

// ======= IPC: 读取本地文件为 data URL =======
ipcMain.handle('read-file-as-url', async (event, filePath) => {
  try {
    const data = await fs.promises.readFile(filePath);
    const ext = path.extname(filePath).toLowerCase();
    let mime = 'application/octet-stream';
    const mimeMap = {
      '.pdf':'application/pdf', '.docx':'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
      '.xlsx':'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      '.pptx':'application/vnd.openxmlformats-officedocument.presentationml.presentation',
      '.png':'image/png', '.jpg':'image/jpeg', '.jpeg':'image/jpeg', '.gif':'image/gif',
      '.bmp':'image/bmp', '.webp':'image/webp', '.svg':'image/svg+xml',
      '.txt':'text/plain', '.html':'text/html', '.htm':'text/html',
      '.json':'application/json', '.xml':'application/xml',
      '.zip':'application/zip', '.dwg':'application/acad', '.dxf':'image/vnd.dxf',
    };
    if(mimeMap[ext]) mime = mimeMap[ext];
    const base64 = data.toString('base64');
    return `data:${mime};base64,${base64}`;
  } catch(e) {
    return null;
  }
});

// ======= 应用启动 =======
app.whenReady().then(async () => {
  createTray();
  createMainWindow();

  app.on('activate', () => {
    if (mainWindow === null) createMainWindow();
    else mainWindow.show();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});

app.on('before-quit', () => {
  if (tray && !tray.isDestroyed()) tray.destroy();
});
