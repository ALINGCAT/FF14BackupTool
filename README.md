# FF14 备份工具

面向 **最终幻想14 国服** 的本地角色设置备份 / 恢复工具。

- 单文件 `FF14BackupTool.exe`，无需安装器
- 启动时通过 Windows UAC 请求管理员权限，避免备份/恢复过程中因目录权限不足而需要重启
- 基于 Windows 自带的 .NET Framework 4.8，不需要额外安装运行时
- 完全离线：不登录账号、不访问网络、不读取游戏内存
- 备份为 `.ffbak`（标准 ZIP + `manifest.json` + SHA-256 校验）
- 支持同角色恢复、跨角色覆盖、系统设置安全合并、便携转移包

## 主要功能

### 备份

选择一个角色，勾选要备份的文件组，生成 `.ffbak`。

支持的文件组：

| 文件组 | 文件 |
|---|---|
| 界面/HUD | `ADDON.DAT` |
| 角色设置 | `COMMON.DAT`、`CONTROL0.DAT`、`CONTROL1.DAT` |
| 热键/键位 | `HOTBAR.DAT`、`KEYBIND.DAT` |
| 用户宏 | `MACRO.DAT` |
| 套装/肖像 | `GEARSET.DAT` + `UISAVE.DAT`（成对使用） |
| 物品/雇员 | `ITEMFDR.DAT`、`ITEMODR.DAT` |
| 消息窗口设置 | `LOGFLTR.DAT` |
| 聊天记录/悄悄话 | `log/`、`ACQ.DAT`（默认不导出） |
| 九宫幻卡 | `GS.DAT` |
| 其他 | 角色文件夹内未识别的文件 |

角色名会尝试从本地 `log` 文件夹中离线解析（职业转换消息优先，其次队伍/团队/跨服通讯贝聊天发送者）。解析不到时显示角色 ID，可手动设置备注名。

### 系统设置

默认勾选，但只备份/合并安全段落：

- 声音、字体、鼠标、UI、过场、手柄按键

不保存、不覆盖：

- 显示设置（分辨率、全屏模式、屏幕位置）
- 画面/图形设置（含 DX11、显卡适配器、画质）
- 手柄设备 GUID
- 网络登录 / WorldId / 端口

恢复时保留目标机器的显示和画面设置，把安全段落合并进目标 `FFXIV.cfg`。写入前会备份原文件，并做段落校验。

### 恢复

- 可直接选择 `.ffbak`
- 也可以选择包含 `.ffbak` 的 `.zip` 便携包
- 可用 `.zip` 内的一个或多个 `.ffbak`；多个时会让用户选择
- 默认按相同 `FFXIV_CHR` 角色 ID 恢复
- 支持把备份中的角色覆盖到另一个目标角色
- 支持手动添加目标角色 ID（即使目标文件夹不存在）
- 跨角色覆盖时默认跳过 `UISAVE.DAT`、`log/`、`ACQ.DAT`
- 跨角色覆盖前会自动完整备份目标角色文件夹
- 恢复失败时尽量自动回滚，并保留原文件备份目录

### 便携转移包

勾选 **“备份与工具一起打包”** 后，除了 `.ffbak`，还会生成：

```text
xxx_便携包.zip
├─ FF14BackupTool.exe
└─ xxx.ffbak
```

外层 ZIP 只包含工具和备份，不含说明或版本文本。到新设备解压后运行 exe，可以直接选择这个 ZIP 导入。

## 使用

1. 打开 `FF14BackupTool.exe`
2. 选择游戏安装目录（包含 `game` 文件夹），或直接选择配置目录
3. 点击“扫描角色”
4. 勾选角色和文件组
5. 选择保存位置，点击“开始备份”

国服常见配置目录：

```text
游戏目录\game\My Games\FINAL FANTASY XIV - A Realm Reborn
```

国际服/文档重定向：

```text
%USERPROFILE%\Documents\My Games\FINAL FANTASY XIV - A Realm Reborn
```

## 构建

要求：

- Windows 10 1903+ / Windows 11
- .NET Framework 4.8（系统自带）
- .NET SDK 8.0 或更高（仅构建需要）
- Visual Studio 不是必需的

```powershell
dotnet build FF14BackupTool.sln -c Release
```

输出：

```text
src\FF14BackupTool\bin\Release\net48\FF14BackupTool.exe
```

Release 构建约 160 KB。项目使用普通的 .NET Framework WPF，不引第三方 UI 库，不生成 DLL。Release 构建会删除 `.exe.config`，保持单文件。

冒烟测试：

```powershell
dotnet run --project tools\SmokeTest\FF14BackupTool.Smoke.csproj -c Release
```

## `.ffbak` 格式

`.ffbak` 本质是 ZIP：

```text
manifest.json
payload/
  FFXIV_CHR.../
    ADDON.DAT
    ...
    log/
      xxx.log
system/
  FFXIV.cfg.safe
```

`manifest.json` 记录：

- 格式版本
- 创建时间 / 工具版本
- 客户端类型
- 角色 ID、备注名、识别到的名字/职业
- 每个文件的相对路径、大小、SHA-256、修改时间、文件组
- 系统设置安全段落列表

恢复时会校验清单、文件大小和哈希；拒绝路径穿越、绝对路径、非法 Windows 文件名的归档条目。

## 已知限制

- 仅支持 Windows 10 1903+ / Windows 11（依赖系统自带 .NET Framework 4.8）
- 单包上限 3 GB；聊天记录默认关闭，通常不会接近上限
- `.ffbak` 不加密，包含聊天记录/宏/联系人等隐私数据时请自行保管
- 跨角色 `UISAVE.DAT` 可能不完整；默认跳过，可手动包含并确认风险
- 角色名只能从本地日志尽力解析；没有日志时使用 ID 或手动备注
- 未做自动更新；请从可信来源获取工具

## 第三方说明

角色名离线解析逻辑参考了 MIT 协议的 [MisakaCirno/ff14_config_editor](https://github.com/MisakaCirno/ff14_config_editor) 对国服客户端日志格式的处理。详细声明见 `THIRD_PARTY_NOTICES.md`。
