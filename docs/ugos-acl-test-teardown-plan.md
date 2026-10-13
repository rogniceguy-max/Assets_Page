# UGOS 隔离 ACL 测试环境清理方案（待批准，尚未执行）

日期：2026-10-13。范围：清理 2026-10-13 隔离 ACL 验证在 NAS 上留下的**测试对象**。
**本方案尚未执行**；执行前需逐项确认。不清理生产对象、不清理历史遗留账号。

> 账号显示名与手机号样账号在本文档中使用占位符（`[TEST-FILM]`、`[TEST-WRITING]`、`[TEST-VISITOR]`、`[REDACTED-ACCOUNT-ID]`），与 `docs/ugos-acl-validation.md` 一致。

---

## 1. 待清理对象清单

| 类别 | 对象 | 说明 | 风险 |
|---|---|---|---|
| 账号 | `[TEST-FILM]` (uid 1005) | 部门补充组 `my_acl_film` | 低 |
| 账号 | `[TEST-WRITING]` (uid 1006) | 部门补充组 `my_acl_writing` | 低 |
| 账号 | `[TEST-VISITOR]` (uid 1007) | 无部门组 | 低 |
| 组 | `my_acl_film` (gid 1006) | 账号删除后方可删 | 中：需确认无其他成员 |
| 组 | `my_acl_writing` (gid 1007) | 同上 | 中 |
| 共享 | `/volume1/MuYing_ACL_Test` 及其 SMB 定义 | 测试共享，含 valid users / write list 改动 | **高：删除共享 API 会重生成全局 `smbshare.conf`** |
| 文件 | `/volume1/MuYing_ACL_Test/` 下全部探针产物 | 夹具、ACE 测试文件 | 低 |
| SMB 配置 | 测试共享自己的 valid users / write list 条目 | 仅测试共享；加过两部门组与参观用户 | 中 |
| 本地 | `wpf-acl-acceptance-20261013/`、探针 `bin/obj`、`ugos-account-investigation/` 脚本 | 本地临时产物 | 无 |
| 标记 | NAS 上 `ROOT/minimal-panic-stop.json`（若存在） | 探针 panic 保护标记 | 无 |

**明确不在清理范围（不得删除）：**

- 生产 `/volume1/Assets`、`01_影视资源库`、`02_文学创作库` 及其 ACE。
- 生产 `smbshare.conf` 中 `[Assets]` 段的 `@group_assets`（这是此前经批准补回的修复，必须保留）。
- 任何历史账号、`123`、`[REDACTED-ACCOUNT-ID]` 等既有账号。
- `group_assets`、`@admin` 等既有组。

## 2. 执行顺序（每步后校验）

1. **停用测试侧服务/连接**：确认无进行中的传输；关闭本地测试客户端与测试 API。
2. **备份现状**（只读导出，不改）：
   - 三个账号与两个组的完整属性；
   - 测试共享的 SMB 定义；
   - **完整 `smbshare.conf`**（用于第 5 步的差异比对，不得整文件回写）；
   - 测试目录完整 ACE 与文件清单。
3. **删除测试共享的文件内容**：仅 `/volume1/MuYing_ACL_Test/` 下的探针产物。不做递归生产目录操作。
4. **删除测试共享定义**：通过 UGOS 原生共享管理接口。
5. **⚠️ 立即比对生产 SMB 配置**：共享删除可能触发全局 `smbshare.conf` 重生成。
   - 逐条比对第 2 步的备份与当前配置；
   - 若 `[Assets]` 段的 `valid users` / `write list` 丢失 `@group_assets`，**只补回该条目**，不整文件覆盖；
   - 结果留档为证据。
6. **删除账号**：`[TEST-VISITOR]`、`[TEST-WRITING]`、`[TEST-FILM]`。
   - 用 UGOS 原生用户管理接口，不直接写厂商数据库。
   - 每删一个后确认其 Samba 凭据同步失效。
7. **删除组**：`my_acl_film`、`my_acl_writing`（确认成员为空后）。
8. **清理 NAS 标记文件**：`ROOT/minimal-panic-stop.json`（若存在）。
9. **清理本地临时产物**：`wpf-acl-acceptance-20261013/`、探针 `bin/obj`、`ugos-account-investigation/`。
   - **注意**：`ugos-account-investigation/` 是证据来源目录，其canonical副本已提交在 `docs/validation/ugos-acl-2026-10-13/`；删除前确认这些副本完整。
10. **最终校验**：生产目录 ACE/mode 同基线；`[Assets]` 段语义同基线；`smbd` 主服务 `active`；三个测试账号与两个组均已不存在。

## 3. 回退方案

- 删除共享若导致生产 `[Assets]` 段丢失 `@group_assets`：按第 2 步备份逐条补回（不整文件覆盖）。
- 账号/组误删：按第 2 步导出的属性用 UGOS 原生接口重建；密码需重新设置（**不保留明文**）。
- 若删除过程中 `smbd` 出现异常：停止后续步骤，先恢复 SMB 配置并确认生产可用，再决定是否继续。

## 4. 执行前必须确认

1. 三个测试账号与两个组**确认无其他用途**（不被其它测试或生产流程依赖）。
2. 测试共享**确认只被本轮验证使用**。
3. 已取得**修改生产 SMB 配置的授权**（第 5 步可能触及）。
4. 明确执行时间窗（避免与生产使用高峰重叠）。
5. 备份已完成并**校验可读**（不是只生成文件）。

## 5. 当前状态

- 清理**尚未执行**（`docs/ugos-acl-validation.md` 的「清理与回退（尚未执行）」同此状态）。
- 生产目录 ACE/mode 与 `[Assets]` 段语义据现有证据同基线；但 `final-production-check.json` 的布尔结论自相矛盾，**需重跑校验后再引用**（见 `docs/ugos-acl-validation.md`「证据一致性待核」）。
