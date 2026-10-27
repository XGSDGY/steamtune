# 上架 Steam 全流程（SteamTune）

按顺序做，前面的步骤会产出后面需要的 AppID / DepotID。

## 0. 前置条件

- Steamworks 合作伙伴账号（个人或公司均可），完成税务与收款信息；
- 每款应用 **$100 Steam Direct** 费用（可回收：累计调整后收入超过 $1,000 后返还）；
- 付款后有 **30 天** 才能正式发售，够时间做审核与测试；
- Windows 10 1809+ 的测试机（SMTC 是硬依赖）。

## 1. 创建应用与 Depot

1. Steamworks → **Create New App**，类型选 **Software**（工具类应用也可向 Valve 申请 `Tool` 类型，需人工审批；不确定就先按 Software）。
2. 记下 **AppID**；在 **SteamPipe → Depots** 新建一个 Windows depot，记下 **DepotID**。
3. **Installation → General → Launch Options**：
   - Executable: `SteamTune.exe`
   - Arguments: 留空
   - OS: Windows
4. 在 **Steamworks → App Admin → Community → Rich Presence** 准备本地化（下一步上传）。

## 2. 联调（还没发售也能用真实 AppID 跑）

新应用的 AppID 对**拥有该应用权限的开发者账号**立即可用，不需要先发售：

```powershell
"<你的 AppID>" | Out-File -Encoding ascii steam_appid.txt   # 项目根目录
powershell -ExecutionPolicy Bypass -File native\build.ps1   # 会把它一起复制到 dist\
.\dist\SteamTune.exe
```

- 必须 **Steam 客户端正在运行**；程序会依次尝试 `SteamAPI_Init` / `SteamAPI_InitSafe` / `SteamAPI_InitFlat`；
- 界面右上角应显示「Steam 已连接」，Steam 卡片显示账号名、AppID 与「API 正常」；
- 若显示「预览模式」，横幅里会写出具体失败原因（Steam 没开、AppID 不属于当前账号、缺少 `steam_api64.dll` 等）；
- 纯调界面：`.\dist\SteamTune.exe --preview`；
- 命令行验证识别结果：`.\dist\SteamTune.exe --once`；
- 发布版不需要 `steam_appid.txt`：直接双击 exe 时 `SteamAPI_RestartAppIfNecessary` 会把用户拉回 Steam 启动流程。

## 3. 上传 Rich Presence 本地化（决定好友看到什么）

好友列表里的文字 100% 来自这里的模板，**不上传就什么都不显示**。

1. **Steamworks → 你的应用 → Edit Steamworks Settings → Community → Rich Presence**；
2. 上传 `steam/richpresence_localization.json`（若后台只接受 KeyValues，用同目录的 `richpresence_localization.vdf`，内容一一对应）；
3. 点 **Save**，再在应用级别点 **Publish**（只保存不发布不会生效）；
4. 模板与代码里的 `steam_display` 必须一一对应：

| `steam_display` | 占位符 | 中文效果 |
| --- | --- | --- |
| `#Status_Listening` | `%song%` | 正在听 一直很安静 |
| `#Status_ListeningArtist` | `%song%` `%artist%` | 正在听 一直很安静 — 那只猫 |
| `#Status_ListeningWithPlayer` | `%song%` `%artist%` `%player%` | 正在听 一直很安静 — 那只猫 · 来自 网易云音乐 |
| `#Status_ListeningWithPlayerNoArtist` | `%song%` `%player%` | 正在听 一直很安静 · 来自 网易云音乐 |
| `#Status_Default` | `%status%` | 兜底/占位 |

代码侧对应 `native/Presence.cs` 的 `Build()`：没有歌手时自动退到不带 `%artist%` 的模板，避免出现「正在听 X — 」这种悬空破折号。**改模板文字不需要重新上传构建**，改完 Publish 即可。

> 语言键用 `english` / `schinese` / `tchinese`（与 Steam 客户端语言名一致）。好友用哪种语言，就看到哪种模板。

## 4. 打包

```powershell
powershell -ExecutionPolicy Bypass -File native\build.ps1 -Clean
```

产物 `dist\`：

```
SteamTune.exe                  主程序（winexe，约 130 KB）
steam_api64.dll                Steamworks 官方运行库
richpresence_localization.json 本地预览用（与后台模板一致）
steam_appid.txt                ← 开发残留，见下面清单
```

打包后自查：

- [ ] `SteamTune.exe` 与 `steam_api64.dll` **同级**；
- [ ] `dist\` 里**没有** `steam_appid.txt`（或确认 depot 已排除）；
- [ ] 图标已内嵌（`native\build.ps1` 用 `build\icon.ico` 作为 win32icon）；
- [ ] 干净机器上双击 exe 会被 Steam 拉起，界面显示「Steam 已连接」；
- [ ] 没有额外运行库依赖（.NET Framework 4.8 是 Windows 10 1809+ 系统自带）。

## 5. 用 SteamPipe 上传

1. 填 `steam/app_build.vdf` 与 `steam/depot_build.vdf` 里的 `<APPID>`、`<DEPOTID>`；
2. 安装 [steamcmd](https://developer.valvesoftware.com/wiki/SteamCMD)，上传：

```powershell
steamcmd.exe +login <你的 Steamworks 账号> +run_app_build "<项目路径>\steam\app_build.vdf" +quit
```

3. 到 **Steamworks → SteamPipe → Builds**，把新 build 设为 **default** 分支（`app_build.vdf` 里的 `setlive` 留空即手动设置，更安全）；
4. 用测试账号在客户端安装，确认好友列表表现。

## 6. 商店页面素材

- 至少 5 张 1920×1080 截图（主界面、好友列表效果对比、设置页、多播放器识别、日志）；
- 头图 616×353、胶囊图 467×181 / 460×215 / 231×87、库图 600×900；
- 描述里说清楚：**读取 Windows 媒体会话识别播放器**、**文字显示在好友列表**、**不上传歌曲数据**；
- 系统需求：Windows 10 1809+、约 5MB 磁盘、无需额外运行库；
- 内容调查按实际填写（不含成人/暴力内容，会读取本机媒体元数据）。

## 7. 审核要点（容易被打回的地方）

- 不要尝试修改、注入、代理 Steam 客户端；所有展示都走 `ISteamFriends::SetRichPresence`；
- 不要伪造「正在玩某款游戏」——只上报自己的 AppID；
- 不要申请管理员权限；读取 SMTC 不需要提权；
- 隐私说明必须与行为一致：歌曲信息仅在本机与 Steam 之间流转；
- 首次发售前有 30 天等待期，审核通过后仍需手动点 Release。

## 8. 发布后的维护

| 改动 | 是否需要重新上传构建 |
| --- | --- |
| 改 rich presence 文案/加语言 | 不需要，后台 Publish 即可 |
| 改识别逻辑、界面 | 需要新 build（`native\build.ps1` 后重新上传） |
| 加播放器映射 | 需要新 build（`native/Detector.cs` 的 `PlayerRules`） |

## 9. 验收清单（发售前）

- [ ] 网易云 / QQ 音乐 / Spotify / 浏览器网页播放 各测一遍，播放器名正确；
- [ ] 暂停 → 恢复：`onPaused=keep` 时好友列表不闪断；
- [ ] 完全退出播放器 → 状态被清除（不是卡在最后一首）；
- [ ] 超长歌名（>100 字符）不报错、不撑破客户端显示；
- [ ] 没有歌手字段的曲目（部分播客/视频）不出现悬空破折号；
- [ ] 好友视角：中文客户端 + 英文客户端各看一次；
- [ ] 关闭 SteamTune → 好友列表状态消失；
- [ ] 断网重连后状态能恢复；
- [ ] 最小化到托盘后仍在同步，托盘菜单可恢复/退出。

## 10. 排查：好友列表只有 “In-Game: xxx”，没有歌名

按顺序检查：

1. **AppID 是不是 480？** 480 是 Spacewar（Valve 测试应用），它没有本应用的 rich presence 模板，
   所以只会显示 `In-Game: Spacewar`。换成你自己的 AppID。
2. **本地化上传了吗？** Steamworks → 你的应用 → Edit Steamworks Settings → Community → Rich Presence →
   上传 `steam/richpresence_localization.json`（或 `.vdf`）。
3. **Publish 了吗？** 只点 Save 不生效，必须在应用级别点 **Publish**；发布后不需要重传构建，
   但客户端可能要重开好友列表（F5 刷新）才看到。
4. **token 名对得上吗？** 代码里 `steam_display` 用的是 `#Status_ListeningWithPlayer` 等；
   本地化文件里必须有完全同名（含 `#`）的条目。
5. **占位符写对了吗？** 模板里是 `%song%`，代码里设置的 key 必须叫 `song`（不是 `Song`、`title`）。
6. **App 还没发售能不能看到？** 开发者账号自己的好友列表立即可见（好友的客户端会按 AppID 拉本地化）；
   如果好友看不到，确认他们的客户端语言对应模板里配置的 `english` / `schinese` / `tchinese`。

界面上「好友列表里看到的」如果显示 `AppID 480 没有本应用的模板…`，说明就是第 1 条。
