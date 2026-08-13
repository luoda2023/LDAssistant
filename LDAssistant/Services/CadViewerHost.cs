using System;
using System.IO;

namespace LDAssistant.Services
{
    /// <summary>
    /// 定位已构建的 cad-viewer（mlightcad/cad-viewer）静态站点，并生成 WebView2 虚拟主机地址。
    /// 该查看器为纯前端（浏览器端解析 DWG/DXF，无需后端），通过 WebView2 虚拟主机映射以 https 方式提供，
    /// 从而支持 ES Module / WASM 正常加载；DWG 文件本身由 C# 经 WebMessage 以 base64 推送给页面。
    /// </summary>
    public static class CadViewerHost
    {
        /// <summary>WebView2 虚拟主机名（仅本进程内生效，映射到本地文件夹）</summary>
        public const string VirtualHost = "cadviewer.local";

        /// <summary>解析 cad-viewer 构建产物目录：优先随程序部署的目录，其次工作区源码构建输出（开发期）。</summary>
        public static string ResolveViewerDir()
        {
            // 1) 随程序部署的目录（dist_final/cad-viewer）
            var deployed = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cad-viewer");
            if (Directory.Exists(deployed) && File.Exists(Path.Combine(deployed, "index.html")))
                return deployed;

            // 2) 工作区源码构建输出（开发期，未部署时）
            var dev = @"D:\ZCODE\cad-viewer\packages\cad-viewer-example\dist";
            if (Directory.Exists(dev) && File.Exists(Path.Combine(dev, "index.html")))
                return dev;

            return null;
        }

        /// <summary>查看器入口 URL（基于虚拟主机）</summary>
        public static string Url => $"https://{VirtualHost}/index.html";
    }
}
