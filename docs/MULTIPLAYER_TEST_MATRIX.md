# 多人回滚与重连测试矩阵

## 统一前置条件

每个场景必须：

1. 主机和客户端完全退出游戏。
2. 安装同一个 ZIP，并核对 DLL SHA-256。
3. 只启用双方一致的模组集合。
4. 完全重启游戏，新建房间并进入新战斗。
5. 游戏启动后，双方用相同测试 ID 创建各自日志会话。
6. 每个测试 ID 只执行一个场景，失败后不复用进程或战斗。

创建会话示例：

```bash
tools/test-session.sh new host rewind CR-20260715-001
tools/test-session.sh new client rewind CR-20260715-001
```

## 场景

| ID | 操作 | 必须通过 |
|---|---|---|
| `baseline` | 双方各正常出牌并结束第一回合 | 无原生同步错误；双方进入同一回合；环境比较通过 |
| `rewind` | 第二回合发起回滚并全票同意 | 双方都出现完成提交；没有 validation、counter 或 hash 错误；回滚后能继续出牌 |
| `reconnect` | 客户端战斗中掉线，再从 Join Friend/邀请加入 | 恢复完成；客户端第一张牌不触发不同步；玩家身份和牌堆归属正确 |
| `rewind_then_reconnect` | 成功回滚后让客户端掉线并重连 | 回滚提交与重连提交都成功；重连后回合、手牌、怪物顺序和宠物状态正确 |
| `reconnect_then_rewind` | 先完成重连，再发起投票回滚 | 重连后的客户端能参与投票；双方完成回滚提交并继续出牌 |
| `rewind_abort_recovery` | 在隔离开发环境中制造一次回滚验证失败 | 双方都恢复事务前安全快照；回合阶段、手牌和本地身份恢复；没有半提交 |
| `dynamic_entities` | 选择会召唤、移除或改变怪物 move 的战斗 | 怪物数量和顺序不变；无 `UNSET_MOVE`；所有存活怪物都有正确意图 |
| `rng` | 记录随机抽牌/目标/怪物行动，回滚后重走相同输入 | 双端结果一致；回滚后下一次随机结果与原时间线一致 |
| `pet_cooldown` | 在宠物 cooldown 变化前后建立锚点并回滚 | 锚点输入状态、重放结果和失败恢复后的 cooldown 均与原时间线一致 |

## 每场测试结束

双方分别执行：

```bash
tools/test-session.sh collect test-artifacts/<测试ID>-<角色>
tools/test-session.sh check test-artifacts/<测试ID>-<角色>
tools/test-session.sh bundle test-artifacts/<测试ID>-<角色>
```

把两个会话解压到同一台机器后执行：

```bash
tools/test-session.sh compare \
  test-artifacts/<测试ID>-host \
  test-artifacts/<测试ID>-client
```

只有以下条件同时满足才能判定通过：

- 双方 `check` 没有已知错误签名。
- 游戏版本、CombatReconnect 版本、协议和 DLL hash 一致。
- 安装模组和当前启动日志中的实际启用模组一致。
- 双方时间线出现对应的回滚/重连完成提交。
- 操作结束后可以继续出牌，而不是只停留在看似成功的 UI 状态。

`PASS_NO_KNOWN_ERRORS` 只表示没有发现已知错误，不等于场景已完成；最终仍需按本页的“必须通过”列检查游戏行为和完成提交日志。

