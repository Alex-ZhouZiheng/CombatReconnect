# CombatReconnect 构建与测试

这些工具只负责构建、安装、收集证据和判定已知错误，不修改回滚协议或战斗状态。

## 构建与发布

```bash
cd /path/to/CombatReconnect
tools/combat-reconnect.sh release
tools/combat-reconnect.sh install
tools/combat-reconnect.sh verify-installed
```

`release` 会依次构建 DLL、使用游戏自带 Godot 运行时生成 PCK、创建 ZIP，并验证源码与发布包。为了避免覆盖运行中的程序集，生成 PCK 和安装前必须关闭游戏。`install` 是独立命令，不会被 `release` 隐式执行。

可通过 `STS2_GAME_DIR`、`STS2_MOD_DIR`、`DOTNET` 覆盖默认路径。

不启动游戏的工具自检：

```bash
tools/test-tooling.sh
```

## 日志会话

测试前创建会话。双端必须使用相同测试 ID：

```bash
tools/test-session.sh new host rewind CR-20260715-001
tools/test-session.sh new client rewind CR-20260715-001
```

请在双方完全启动游戏后再创建会话。命令会记录当前日志字节位置、游戏版本、协议、DLL SHA-256、已安装模组，以及当前 `godot.log` 中实际初始化成功的模组。

测试结束后分别执行：

```bash
tools/test-session.sh collect test-artifacts/CR-20260715-001-host
tools/test-session.sh check test-artifacts/CR-20260715-001-host
tools/test-session.sh bundle test-artifacts/CR-20260715-001-host
```

将朋友端 ZIP 放到本机后，可以比较双方环境和关键时间线：

```bash
tools/test-session.sh compare \
  test-artifacts/CR-20260715-001-host \
  test-artifacts/CR-20260715-001-client
```

环境比较通过不代表战斗状态一定正确；仍需检查 comparison 文件中按时间汇总的 hash、计数器、提交和恢复记录。

## 单人 F6 自动触发

当前游戏版本为 `v0.108.0`。截至 2026-07-15，STS2MCP 最新源码针对当前 `sts2.dll` 编译有 11 个错误，独立 STS2MenuControl 也不支持当前 macOS 引用路径。两者都不应安装到正式回滚测试环境。完整检查记录见 [AUTOMATION_COMPATIBILITY.md](AUTOMATION_COMPATIBILITY.md)。

先检查控制后端：

```bash
tools/test-session.sh controller-check
```

进入单人战斗第 2 回合或更晚后，可以自动发送 F6、等待结果、检查并打包日志：

```bash
tools/test-session.sh single-selftest 180
```

macOS 首次运行可能要求允许终端控制“系统事件”。该命令不会自动创建存档或替玩家完成第一回合。

## 双端测试矩阵

```bash
tools/test-session.sh matrix
```

执行顺序：

1. `baseline`：确认同版本、同模组集合的新房间可以完成一回合。
2. `rewind`：只测试投票回滚。
3. `reconnect`：只测试断线重连，并在重连后出一张牌。
4. `rewind_then_reconnect`：回滚提交后断线重连。
5. `reconnect_then_rewind`：重连提交后投票回滚。
6. `rewind_abort_recovery`：验证失败恢复。
7. `dynamic_entities`：覆盖召唤怪物、怪物顺序和动态 move。
8. `rng`：检查回放后的后续随机结果。
9. `pet_cooldown`：检查宠物输入状态和 cooldown。

每个场景必须新建房间和战斗，不能复用上一次失败后的进程状态。主机和客户端分别创建会话、收集日志、运行 `check`，最后在一台机器上执行 `compare`。

逐场操作和通过条件见 [MULTIPLAYER_TEST_MATRIX.md](MULTIPLAYER_TEST_MATRIX.md)。

完全不熟悉电脑的 macOS 客户端可以使用 [朋友端测试指南.md](朋友端测试指南.md) 和双击式测试包。测试包由以下命令生成：

```bash
tools/build-friend-test-kit.sh
```
