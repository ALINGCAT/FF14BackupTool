# FF14 备份工具

面向 **最终幻想14 国服** 的本地角色设置备份 / 恢复工具。

- 单文件 `FF14BackupTool.exe`，无需安装器
- 支持同角色恢复、跨角色覆盖、系统设置安全合并、便携转移包

## 主要功能

### 备份

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

外层 ZIP 只包含工具和备份。到新设备解压后运行 exe，可以直接选择这个 ZIP 导入。

## 构建

要求：
- .NET SDK 8.0 或更高（仅构建需要）

```powershell
dotnet build FF14BackupTool.sln -c Release
```

输出：

```text
src\FF14BackupTool\bin\Release\net48\FF14BackupTool.exe
```

## 说明

角色名离线解析逻辑参考了 MIT 协议的 [MisakaCirno/ff14_config_editor](https://github.com/MisakaCirno/ff14_config_editor) 对国服客户端日志格式的处理。详细声明见 `THIRD_PARTY_NOTICES.md`。
