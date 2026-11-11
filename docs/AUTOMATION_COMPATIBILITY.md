# 外部自动化兼容性基线

检查日期：2026-07-15

当前游戏：`v0.108.0`，commit `58694f64`，Godot `4.5.1-m.14`，macOS arm64。

## STS2MCP

- 检查 commit：`20eadebde358a37cca41f8b38728099e6d0d19db`
- 仓库声明测试版本：STS2 `v0.103.2`
- 目标框架：`net9.0`
- macOS 游戏引用：已支持
- 对当前 `sts2.dll` 构建结果：失败，11 个编译错误

断裂接口包括：

- `CombatManager.IsPlayPhase` 已不存在。
- 多处 API 从 `CombatState` 改为 `ICombatState`，调用方签名未更新。
- `MerchantRoom.Inventory` 已不存在。

结论：不能安装到 CombatReconnect 的正式回滚/重连测试环境。即使只使用菜单 API，完整 DLL 仍无法针对当前游戏构建；使用旧发布 DLL 会把未验证的 Harmony、HTTP 操作和多人动作逻辑带入测试。

## STS2MenuControl

- 检查 commit：`a37f56786ecd2fcbcb8de391cf18142de00ee292`
- 目标框架：`net9.0`
- 对当前 macOS 构建结果：失败，49 个编译错误

项目文件只引用 Windows 的 `data_sts2_windows_x86_64`。此外，新版 STS2MCP 已经覆盖菜单和多人大厅 API，继续同时维护或加载独立 MenuControl 会增加重复的菜单反射和多人 UI 操作面。

结论：不接入，也不为 CombatReconnect 单独移植。

## 当前安全后端

单人自动化采用以下边界：

1. 玩家手动进入第 2 回合或更晚的单人战斗。
2. `tools/test-session.sh single-selftest` 记录日志偏移和环境指纹。
3. 脚本通过 macOS System Events 发送 F6。
4. 脚本等待 `SELFTEST PASS` 或 `SELFTEST FAIL`，只归档本次测试增量日志。

它不能自动创建新存档或替玩家完成第一回合，但不会向游戏安装额外控制模组。游戏版本变化后，先重新编译 STS2MCP/MenuControl；只有构建通过并完成隔离运行测试，才能更新这份基线。

