# MuYing 资产盘门户改造与工程质量验收报告

> **目标仓库**：[rogniceguy-max/Assets_Page](https://github.com/rogniceguy-max/Assets_Page)  
> **工作分支**：`feat/visual-design-system-gsap`  
> **验证服务**：`http://192.168.1.153:8080/`

---

## 一、真实工程执行与验证结果 (Real Verification Results)

### 1. 构建阻塞修复 (Build Blocker Resolved)
- **根本原因定位**：根目录 `.gitignore` 原有 `lib/` 规则未添加斜杠前缀，导致全局匹配并误忽略了关键工具库 `frontend/src/lib/utils.ts`。在任何新环境中克隆仓库执行 `npm ci && npm run build` 时均会因缺失 `cn()` 工具函数引发 TypeScript 编译崩溃。
- **修复措施**：
  - 更新 `.gitignore` 规则为 `/lib/`，并显式加入非忽略保护规则 `!frontend/src/lib/`。
  - 成功纳管并暂存 [`frontend/src/lib/utils.ts`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/src/lib/utils.ts)。
  - 执行 `npm ci`（140 个依赖包秒级锁定安装）。
  - 执行 `npm run build`：**1584 模块转换，0 Error / 0 Warning 构建通过**。

### 2. GSAP Tab 动画鲁棒性重构
- **单一事实来源 (Single Source of Truth)**：选中状态纯粹由 React 状态 `tab` 驱动，指示器位移收敛至 `useLayoutEffect`。
- **减弱动效模式完整适配**：在 `prefers-reduced-motion: reduce` 环境下，指示器通过 `gsap.set` **瞬时精准就位**（实测 transform 为 `matrix(1, 0, 0, 1, 210, 0)`），不再出现指示器卡死原处的缺陷。
- **规避卸载 DOM 动画**：表单切换采用挂载后容器级别的微淡入（`formPanelRef`），GSAP 生命周期受 `ctx.revert()` 保护，彻底杜绝操作已卸载 DOM 节点的风险。
- **快速高频切换验证**：加入 `overwrite: true`，在自动化测试中模拟 40ms 超短间隔连续切换 6 次，界面无任何补间堆叠、抖动或报错。

### 3. CSS 变量作为 Token 唯一事实来源
- 所有表面色（`--background`, `--sidebar`, `--card`, `--secondary`）、描边色（`--border`）、文本色（`--foreground`, `--muted`, `--muted-foreground`）、品牌色（`--accent`）与语义色（`--success`, `--warning`, `--destructive`, `--info`）全部统一定义于 [`frontend/src/index.css`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/src/index.css) 的 `:root` 中。
- [`frontend/tailwind.config.js`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/frontend/tailwind.config.js) 彻底剥离重复硬编码 Hex，全部通过 `var(--...)` 引用，实现了跨模块样式事实来源的高度一致。

### 4. ARIA 语义、焦点可见性与文本对比度合规
- **无障碍语义 (ARIA)**：
  - Tab 容器声明 `role="tablist"`，切换按钮声明 `role="tab"`、`aria-selected`、`aria-controls`，支持键盘左/右方向键切换。
  - 表单区域声明 `role="tabpanel"` 与 `aria-labelledby`。
  - 业务组声明 `role="radiogroup"` 与 `role="radio"`、`aria-checked`，支持键盘方向键单选。
  - 错误提示框声明 `role="alert"` 与 `aria-live="polite"`。
  - 所有表单控件均通过 `id` 与 `<label htmlFor="...">` 严格关联。
- **焦点状态 (Focus Visibility)**：所有交互控件统一配置 `focus-visible:ring-2 focus-visible:ring-accent focus-visible:outline-none`。
- **小字对比度 (WCAG AA)**：提升 `--muted-foreground` 色值至 `#95968E`，在卡片底色（`#242623`）上对比度达到 **5.16:1**（远高于 WCAG AA 正文 4.5:1 要求）；辅助说明文本对比度达 **6.18:1**，主文本对比度达 **12.5:1**。

### 5. 跨视口 Playwright 自动化验证通过清单

| 测试项 | 视口规格 | 验证要点 | 实际执行结果 |
| :--- | :--- | :--- | :--- |
| **桌面端全宽** | 1440 × 900 | 布局、工整度、Tab 与 Radiogroup ARIA | **PASS** |
| **高频连续切换** | 1440 × 900 | 40ms 极短间隔连续连点 6 次无崩溃 | **PASS** |
| **减弱动效模式** | 1440 × 900 | `prefers-reduced-motion` 指示器瞬时就位 | **PASS** (`matrix 210px`) |
| **笔记本端** | 1280 × 800 | 适中空间下的表单与工具栏层级 | **PASS** |
| **移动端紧凑** | 390 × 844 | 纯净单列排版、自适应软键盘与表单 | **PASS** |
| **异常报警状态** | 1440 × 900 | `role="alert"`、`aria-live` 与低饱和红框 | **PASS** |

---

## 二、P0 级安全缺陷处置结果 (Security Vulnerability Resolution)

> [!NOTE]
> **P0 凭据安全隐患已彻底修复并全链路验证通过**：已成功实施 60 秒一次性高熵安全令牌（Ephemeral One-Time Token）与零明文落地存储架构，消除了脚本明文泄露和数据库明文存储风险。

### 1. 修复方案实施细节

1. **废除未授权下载与遍历越权**：
   - 彻底关闭 `GET /api/download-script?name=...`。
   - 所有脚本下载与凭据兑换必须携带服务端下发的单次高熵令牌 `token`。无 Token 或伪造 Token 请求直接返回 `403 Forbidden`。
2. **60 秒一次性凭据兑换与单次消费焚毁 (One-Time Token & RAM Ephemeral)**：
   - 无论「首次注册」还是「重新领脚本（输入姓名+密码验证）」，服务端均通过 [`EphemeralTokenManager`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/core/token_manager.py) 在内存中签发一个 60 秒有效期的安全令牌。
   - 密码**只暂存在内存 RAM 中**，设置 60 秒 TTL 倒计时。
   - 生成的 `.bat` 脚本内**不含任何员工密码**，改由内嵌的一行 PowerShell 命令向 `/api/claim-mount` 动态兑换凭据。
   - 客户端一旦兑换成功，服务端立即在内存中彻底焚毁（清除并注销）该令牌与明文凭据，重放请求（Replay Attack）将直接被拒（403）。
3. **数据库零明文存储 (Zero-Plaintext Database Storage)**：
   - [`backend/database.py`](file:///C:/Users/CHEN/.gemini/antigravity/scratch/Assets_Page/backend/database.py) 在写入员工数据时，`raw_password_encrypted` 强制写入 `NULL`。
   - 服务端启动时执行全量迁移逻辑，将历史存量数据中的明文密码全部清空置为 `NULL`。
   - 员工身份校验严格基于哈希对比。
4. **客户端脚本零明文与内存销毁**：
   - 脚本通过 PowerShell 动态取得临时凭据后，直接注入 Windows 凭据管理器（`cmdkey /add:...`），执行挂载后立即执行 `$p = $null` 销毁内存变量，磁盘上生成的 `.bat` 文件中没有任何密码信息。

### 2. 自动化安全测试验证结果

已在真实 NAS 环境（`192.168.1.153`）执行全量自动化安全验证脚本：
- `GET /api/download-script?name=张三` 未授权请求：拦截成功（返回 `403 Forbidden`）。
- 注册/重新领脚本生成 60s 令牌：成功签发。
- 生成的 `.bat` 脚本内容审计：**100% 不包含任何员工明文密码**。
- PowerShell 客户端动态凭据兑换：成功注入并挂载。
- 令牌二次消费重放攻击：拦截成功（返回 `403: 该一次性令牌已被兑换使用，无法重复调用`）。
- 数据库落盘状态检查：`raw_password_encrypted` 全量为 `None / NULL`。

---

## 三、三合一入口（登录、注册、重新挂载）与工作台体系

根据团队运营流程，对门户主入口进行了功能升级与术语标准化：

1. **全面将「重新领脚本」重命名为「重新挂载」**：
   - 全局前端界面、提示文本、后端接口返回文案及脚本内部安全回退指引，统一采用标准术语「重新挂载」。
2. **新增「登录」入口与三合一卡片设计**：
   - 首页卡片采用标准三栏式 GSAP 动效 Tab 切换：`登录` | `注册` | `重新挂载`。
   - 三段式滑块指示器严格按照 `w-[calc((100%-8px)/3)]` 与 `xPercent: [0, 100, 200]` 进行动力学位移，兼容无障碍键盘左右键循环导航与 `prefers-reduced-motion`。
3. **员工资产工作台（UserDashboard）与全状态协同**：
   - **登录 (Login)**：已注册员工凭全名/系统账号 + 密码登录后，进入高质感暗室风格「员工资产工作台」，直观查看所属业务组、Linux 存储账号、Z: 盘授权状态与挂载指引，并可随时点击获取 60 秒挂载脚本或退出账号。
   - **注册 (Register)**：新员工注册成功后，即时进入 60 秒专属挂载脚本下发卡片，并在顶部工具栏同步展示登录身份徽标。
   - **重新挂载 (Remount)**：针对盘符掉线或更换电脑的员工，输入凭据后直接生成全新 60 秒高熵挂载脚本。
   - **顶部 Header 状态联动**：登录后展示员工姓名、所属业务组徽标与快捷退出按钮。

---

## 四、代码分支与提交状态

- 重构与新增工作全部位于独立分支：`feat/visual-design-system-gsap`
- 远程仓库：[rogniceguy-max/Assets_Page (feat/visual-design-system-gsap)](https://github.com/rogniceguy-max/Assets_Page/tree/feat/visual-design-system-gsap)
- NAS 生产部署：服务 `muying-hub.service` 已热更新重启并稳定运行（`http://192.168.1.153:8080/`）。
