# MuYing 资产盘门户改造与工程质量验收报告

> **目标仓库**：[rogniceguy-max/Assets_Page](https://github.com/rogniceguy-max/Assets_Page)  
> **工作分支**：`feat/visual-design-system-gsap`  
> **验证服务**：`http://192.168.1.153:8080/`

---

## 一、架构重构：Windows 原生凭据挂载与极简无感知体验

根据需求取舍，在保持内网 HTTP 与 Python 后端极简运维的前提下，全面废除了复杂的 60 秒临时 Token 兑换机制与 `/api/claim-mount` 接口，改为 Windows 原生安全凭据管理方案：

### 1. 彻底移除凭据兑换与临时 Token 机制
- **安全废弃与迁移**：
  - 彻底删除 [`backend/core/token_manager.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/token_manager.py) 与 `EphemeralTokenManager`。
  - 下线 `/api/claim-mount` 端点，任何对此接口的请求均直接返回 `410 Gone`。
  - 前端 `RegisterCard`、`SuccessCard`、`UserDashboard` 及 `App.tsx` 彻底剥离 `token` 状态传递。
  - 移除了成功页面的 60 秒倒计时与过期告警，普通员工再无“倒计时紧迫感”或频繁过期反复领取的困扰。

### 2. Windows 11 原生凭据管理与安全持久化
- **兼容性验证与技术选型**：
  - 在真实 Windows 11 环境中验证了 `New-SmbMapping -SaveCredentials`：确认在非 Active Directory 域控的独立 Linux Samba NAS 场景下，该参数会触发系统级报错 `The parameter is incorrect`。
  - **内存级 Win32 API 方案**：在生成的挂载工具中，采用 PowerShell 内存动态绑定 `advapi32.dll` 的 `CredWriteW` API，在用户通过 Windows 原生安全对话框（`$host.UI.PromptForCredential` / `Get-Credential`）输入账号密码后，直接将凭据写入 Windows 凭据管理器（Credential Manager），目标为 `192.168.1.153`。
  - **绝无命令行或磁盘泄密**：密码完全存在于 Windows 系统加密凭据存储中，绝不出现在命令行参数、BAT 脚本内容、日志或环境变量中。
  - **持久化与开机免密**：使用 `CRED_PERSIST_LOCAL_MACHINE`，配合 `New-SmbMapping -Persistent:$true`，重启电脑后 Windows 自动使用已存凭据重连 `Z:` 盘。

### 3. 双击即用 Polyglot 脚本设计
- 采用 `<# : ... #>` CMD 与 PowerShell 混合执行结构，文件以后缀 `.bat` 下发：
  - 普通员工直接双击即可执行，无需开启开发者模式或解除系统 ExecutionPolicy。
  - 全流程普通用户权限运行，无需管理员权限（UAC）。
  - 支持中文字符路径与特殊字符账号。
  - 首次运行：弹窗输入一次凭据 -> 建立 `Z:` 盘映射 -> 桌面自动创建「沐影」快捷方式 -> 自动唤起 Windows 文件资源管理器。
  - 重复运行 / 重新挂载：自动检测已保存凭据并免密直连；若断线优先尝试恢复，无需重复输入密码；若盘符正常直接打开文件夹。
  - 冲突保护：若 `Z:` 盘已被本地物理磁盘或其他网络设备占用，明确提示并安全终止，严禁未经确认删除员工现有网络连接。

---

## 二、P0 / P1 级系统安全缺陷修复

### 1. P0：静态资源路径规范化与目录穿越全方位防御
- **检查位置**：[`backend/server.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/server.py) 中的 `/assets/` 静态文件处理。
- **加固实现**：
  - URL 解码与规范化（`urllib.parse.unquote` + `os.path.normpath`）。
  - 空字符注入防御（检测 `\x00` 直接返回 `400 Bad Request`）。
  - 严格作用域前缀校验（`os.path.realpath` 必须在 `dist/assets` 目录下，阻止任何 `..` 或 `%2e%2e` 穿越，违者返回 `403 Forbidden`）。
  - 静态资源扩展名严格白名单（仅允许 `.js`, `.css`, `.svg`, `.png`, `.jpg`, `.jpeg`, `.ico`, `.woff2`, `.woff`, `.ttf`, `.map`，阻止读取 `.py`, `.db`, `.env` 等敏感文件）。
- **自动化攻击测试结果**：
  - `GET /assets/..%2f..%2fserver.py` -> `403 Forbidden`（拦截成功）
  - `GET /assets/../../server.py` -> `403 Forbidden`（拦截成功）
  - `GET /assets/null%00.js` -> `400 Bad Request`（拦截成功）
  - `GET /assets/data/muying.db` -> `403 Forbidden`（拦截成功）

### 2. P1：密码加盐哈希升级（scrypt）与存量无感迁移
- **算法选型**：使用 Python 3.11 标准库内置的 `hashlib.scrypt`，完全零第三方依赖。
- **安全规格**：每位用户独立分配 16 字节随机 Salt，参数标准为 `n=16384, r=8, p=1`，生成标准化格式 `scrypt$16384$8$1${salt}${key}`。
- **存量平滑迁移**：
  - [`backend/database.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/database.py) 的 `verify_password()` 兼容旧版 SHA-256，返回 `(is_valid, needs_rehash)`。
  - 员工登录时若使用旧哈希，校验通过后自动就地升级并写入新 `scrypt` 哈希，不影响原有 Samba 账户密码，实现完全零停机、无感平滑迁移。
  - 数据库字段 `raw_password_encrypted` 强制置为 `NULL`，服务端启动时自动清理历史可能残留的明文。

### 3. P1：真实客户端 IP 审计
- 废弃对不可信 `X-Forwarded-For` 头的盲目信任。
- 在内网 HTTP 直连架构下，直接基于底层 TCP 套接字地址（`self.client_address[0]`）提取客户端真实物理 IP，有效防范伪造 IP 绕过设备注册频控。

### 4. P1：工作台状态严格区分与人机交互升级
- 在员工工作台（[`UserDashboard.tsx`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/src/components/dashboard/UserDashboard.tsx)）中，明确将「网页工作台登录」与「本地 Windows 驱动器连接」进行视觉与语义拆分：
  - 网页状态：`网页工作台已登录 (账号已授权)`
  - 本地状态：`本地 Z: 盘状态：需运行挂载工具建立映射`
  - 严禁在浏览器端虚构 Windows 映射盘已连接状态。

---

## 三、真实工程构建与全链路自动化验证

### 1. 前端工程构建
- 执行环境：Node.js + Vite + TypeScript
- 构建命令：`npm run build`
- 结果：**1585 个模块完整编译转换，0 Error / 0 Warning，dist 目录生产就绪**。

### 2. 本地与真实 NAS 环境双重验证通过清单

| 验证模块 | 测试项 | 验证条件 | 实际执行结果 |
| :--- | :--- | :--- | :--- |
| **密码安全** | scrypt 哈希生成与校验 | 独立随机盐、标准化格式 | **PASS** |
| **密码安全** | 存量旧版 SHA-256 兼容 | 自动识别 needs_rehash 并就地升级 | **PASS** |
| **脚本安全** | 挂载脚本内容审计 | 零明文密码、零 Token、无 claim-mount | **PASS** |
| **HTTP 安全** | `/assets/` 目录穿越防御 | `%2e%2e`、`..`、空字节注入拦截 | **PASS** (`400/403`) |
| **HTTP 安全** | `/api/claim-mount` 废弃 | 接口已被下线，请求返回已注销状态 | **PASS** (`410 Gone`) |
| **HTTP 安全** | `/api/download-script` | 正常下发带有用户绑定的原生挂载脚本 | **PASS** (`200 OK`) |
| **UI 流转** | 首页 3-in-1 Tab 切换 | 登录 / 注册 / 重新挂载，键盘与动画 | **PASS** |
| **UI 流转** | 响应式多视口 | 1440px / 1280px / 390px 布局正常 | **PASS** |
| **UI 流转** | 员工登录与工作台 | 登录成功进入 UserDashboard，状态明确 | **PASS** |
| **UI 流转** | 挂载指引 SuccessCard | 无 60s 倒计时，展示 Windows 原生挂载指引 | **PASS** |
| **进程管理** | 自动化测试进程闭环 | 测试进程全部正常退出，无悬挂后台任务 | **PASS** |

---

## 四、部署与网络说明

- **部署取舍**：根据团队内部约束，保留当前 HTTP 内网部署（`http://192.168.1.153:8080/`），不引入 HTTPS 反代、证书申请或额外客户端。
- **传输风险说明**：在未启用 TLS/HTTPS 的纯 HTTP 环境下，内网局域网（同一交换机/WiFi 下）的明文 HTTP 流量可能面临局域网嗅探风险。由于团队当前处于受信任的公司私网环境中，此部署取舍已被团队明确接受。
- **分支状态**：所有代码变更已严格同步并验证于分支 `feat/visual-design-system-gsap`。
