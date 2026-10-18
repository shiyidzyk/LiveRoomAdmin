[简体中文](#简体中文) | [English](#english)

---

# 简体中文

# 直播间管理终端（LiveRoomAdmin）

局域网 OBS 推流直播系统：Windows 原生客户端（WPF）+ 内置直播服务器（Node.js + mediamtx + FFmpeg）+ 网页直播间与后台管理。

- 安装后授予管理员权限即可在局域网内直接运行：OBS 向本机推流 → 网页端/客户端实时观看、弹幕、礼物打赏。
- 客户端不内嵌任何浏览器组件，直播画面由 libVLC 原生渲染。
- 单机集成：默认不运行核心服务，点「开始直播」才拉起全部服务，退出自动停止。

## 功能特性

- 网页直播间：账号登录、弹幕聊天、礼物打赏（虚拟代币，模拟微信/支付宝/网银充值）、在线人数/观众列表、全屏播放、警告/封禁大字提示、多直播间切换（顶栏下拉 + ?room=房间key 直达）
- 后台管理（网页 + 客户端）：三级权限（最高管理员 > 管理员 > 观众）、管理员细分授权、创建/封禁/踢出/警告/删除账号、强制下播/恢复/重启服务、多端登录策略与设备下线、服务日志、直播间管理（新建/删除直播间，按房间独立下播/恢复/警告）
- Windows 客户端：实时直播预览、首页状态面板（多直播间选择下拉）、服务日志页、设置页（服务器/外观/语言/网页管理页开关）、关于与完整更新日志、安装向导（语言选择、桌面/开始菜单快捷方式）
- 中英双语界面：安装向导开头选择语言（默认简体中文），设置页可随时切换

## 核心开源依赖

| 组件 | 用途 | 项目 |
|---|---|---|
| mediamtx | RTMP/HLS/WebRTC/RTSP 直播服务 | https://github.com/bluenviron/mediamtx |
| Node.js | HTTP/SSE/API 服务端（账号、弹幕、礼物、管理） | https://nodejs.org/ |
| FFmpeg | 低延迟转码（AAC→Opus → RTSP） | https://ffmpeg.org/ |
| OBS Studio | 推流端（推荐搭配使用） | https://obsproject.com/ |
| hls.js | 网页端 HLS 播放 | https://github.com/video-dev/hls.js |
| LibVLCSharp / VideoLAN.LibVLC | 客户端原生直播播放 | https://github.com/videolan/libvlcsharp |

## 下载与更新日志

> 最新版安装包：**1.02.01.beta**
>
> 下载链接：https://aka.doubaocdn.com/s/uEEb2PEeXS
>
> （安装包约 240MB，超出 GitHub 单文件 100MB 限制，故通过云盘链接分发；版本号与更新日志同步记录于此。）

### 1.02.01.beta（2026-10-18）
- 新增「多直播间」功能：同时开多个直播间，每个直播间独立推流、独立管理、互不干扰
- 客户端首页新增「选择直播间」下拉；推流地址 / 推流密钥 / 网页链接按当前房间动态生成（rtmp://IP:1935/stream/房间key）
- 管理端「直播管理」页新增直播间管理：仅最高管理员可新建 / 删除直播间（主直播间不可删），下播 / 恢复 / 警告按所选房间生效
- 网页直播间顶栏新增房间切换下拉，支持 ?room=房间key 直达；在线人数、弹幕、礼物流按房间隔离
- 客户端 ffmpeg 转码守护改为多房间模型（顺带消除旧守护 30 秒无输出误杀导致的周期性断流）

### 1.02.00.beta（2026-09-29）
- 客户端全面适配双语（简体中文 / English）：界面静态文案迁移到语言资源，切换即时生效
- 安装向导开头新增「选择语言 / Choose Language」询问页（默认简体中文），首次启动自动生效
- 设置 → 外观 → 语言：随时切换界面语言并持久化
- 项目源码开源（本仓库，MIT 协议），README 中英双语

### 1.01.10.beta（2026-09-29）
- 首页右侧新增「网页直播间」提示卡片：开始直播后自动显示网页直播间链接，一键复制

### 1.01.09.beta（2026-09-29）
- 修复推流成功后客户端首页预览无画面：服务端 HLS 改为始终 remux（hlsAlwaysRemux）
- 安装器覆盖安装前自动结束运行中的程序，升级不丢账号数据

### 更早版本
完整历史更新日志请见客户端「设置 → 关于 → 更新日志」（鼠标滚轮滚动查看）。

## 从源码构建

环境：.NET 8 SDK（Windows x64）、NSIS 3.x（安装器）。

```bash
# 发布客户端（自包含）
cd WindowsClient
dotnet publish -c Release -r win-x64 --self-contained true -o publish

# 组装运行时（node/mediamtx/ffmpeg 二进制放入 publish/bins，服务端文件放入 publish/server）

# 编译安装器
cd installer
makensis setup.nsi
```

服务端源码：`src/server.js`；网页端：`LiveStreamApp/app/src/main/assets/public/`；mediamtx 配置：`mediamtx.yml`。

## 许可证

本项目以 MIT 许可证开源，见 [LICENSE](LICENSE)。

---

# English

# LiveRoomAdmin

A LAN live-streaming system for OBS: native Windows client (WPF) + built-in live server (Node.js + mediamtx + FFmpeg) + web room & admin panel.

- Run it directly on your LAN after granting admin permissions: push from OBS → watch in real time, chat, and send gifts on the web or client.
- The client contains **no browser components**; live video is rendered natively with libVLC.
- Integrated architecture: core services stay idle until you click "Start Streaming"; they stop automatically on exit.

## Features

- Web room: account login, danmaku chat, gift tipping (virtual coins with simulated WeChat/Alipay/bank top-up), online viewer list, fullscreen playback, admin warning/ban overlays, multi-room switcher (top-bar dropdown + `?room=key` deep link)
- Admin panel (web + client): 3-tier roles (Super Admin > Admin > Viewer), fine-grained admin permissions, create/ban/kick/warn/delete accounts, force-stop/resume/restart services, multi-device login policy & device kick, service logs, room management (create/delete rooms, per-room stop/resume/warn)
- Windows client: live preview, home status dashboard (multi-room selector), log viewer, settings (server/appearance/language/web-admin switch), about & full changelog, installer with language/desktop/Start menu options
- Bilingual UI (Simplified Chinese / English): pick the language at the start of the installer (Chinese by default), switch anytime in Settings

## Core Open-Source Dependencies

| Component | Purpose | Project |
|---|---|---|
| mediamtx | RTMP/HLS/WebRTC/RTSP live server | https://github.com/bluenviron/mediamtx |
| Node.js | HTTP/SSE/API backend (accounts, chat, gifts, admin) | https://nodejs.org/ |
| FFmpeg | Low-latency transcoding (AAC→Opus → RTSP) | https://ffmpeg.org/ |
| OBS Studio | Streaming source (recommended) | https://obsproject.com/ |
| hls.js | HLS playback on the web | https://github.com/video-dev/hls.js |
| LibVLCSharp / VideoLAN.LibVLC | Native playback in the client | https://github.com/videolan/libvlcsharp |

## Download & Changelog

> Latest installer: **1.02.01.beta**
>
> Download: https://aka.doubaocdn.com/s/uEEb2PEeXS
>
> (The installer is ~240MB, above GitHub's 100MB per-file limit, so it is distributed via cloud link; the version and changelog are tracked here.)

### 1.02.01.beta (2026-10-18)
- New "Multi-Room" feature: run multiple live rooms at once, each with independent stream & management
- Client Home: new "Select Room" dropdown; stream URL / key / web link regenerate per room (rtmp://IP:1935/stream/room-key)
- Admin "Live Room" tab: room management — only Super Admin can create/delete rooms (main room is protected); stop/resume/warn apply to the selected room
- Web room: top-bar room switcher, `?room=key` deep link; viewers, danmaku and gifts are isolated per room
- Client ffmpeg daemon rebuilt for multi-room (also removes the old 30s no-output auto-restart that caused periodic stream drops)

### 1.02.00.beta (2026-09-29)
- Full bilingual UI (Simplified Chinese / English): static texts moved to language resources, switch takes effect instantly
- "Choose Language" page added at the start of the installer (Chinese by default), applied on first launch
- Settings → Appearance → Language: switch anytime, persisted
- Source code open-sourced (this repo, MIT), bilingual README

### 1.01.10.beta (2026-09-29)
- "Web Room" info card on Home: shows the room link after streaming starts, one-click copy

### 1.01.09.beta (2026-09-29)
- Fixed blank preview after successful push: HLS always remux (hlsAlwaysRemux) enabled
- Installer kills running processes before upgrade; account data preserved

### Older versions
Full changelog is available in the client: Settings → About → Changelog (scroll to view).

## Building from Source

Requirements: .NET 8 SDK (Windows x64), NSIS 3.x (installer).

```bash
cd WindowsClient
dotnet publish -c Release -r win-x64 --self-contained true -o publish
# Put node/mediamtx/ffmpeg binaries into publish/bins, server files into publish/server
cd installer
makensis setup.nsi
```

Server source: `src/server.js`; web assets: `LiveStreamApp/app/src/main/assets/public/`; mediamtx config: `mediamtx.yml`.

## License

MIT — see [LICENSE](LICENSE).