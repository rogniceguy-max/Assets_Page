# MuYing 资产盘门户设计系统规范 (MuYing Asset Portal Design System)

> **版本**：v1.0.0  
> **定位**：MuYing 影视传媒团队内部 NAS 资产盘注册、访问与挂载门户。  
> **视觉基调**：专业、克制、编辑感、精确、耐看、低饱和、成熟的桌面生产力软件工具。

---

## 1. 设计哲学与核心原则 (Design Philosophy)

本设计系统摒弃消费级 SaaS 与 AI 概念展示站惯用的“霓虹光晕、高饱和蓝紫渐变、夸张回弹”等浮夸手法，吸纳 DaVinci Resolve、Avid Media Composer、Final Cut Pro 及 macOS Pro 工具的工业美学。

### 1.1 四大核心原则

1. **暗室编辑感 (Darkroom Ergonomics)**  
   影视剪辑、调色与编剧长时间处于弱光工作室环境，界面采用深灰底色阶（#141513 ~ #292C28），严格避免刺眼纯白与高饱和荧光色，降低视觉疲劳。

2. **微明度层级 (Micro-Luminance Hierarchy)**  
   不依赖大面积彩色投影或模糊发光斑点建立层级，而是依靠微小的背景灰阶递进（4阶表面明度）与 1px 低对比度描边（#3B3E38）建立清爽、严谨的深度关系。

3. **单一视觉焦点 (Singular Focus & Restraint)**  
   每一个视图和卡片中，强视觉操作（Primary CTA）只保留一个，并统一使用香槟暖铜金（#BAA17E）作为强调色。其余操作与状态指示退居中性灰阶或微弱低饱和语义色。

4. **触控与桌面精确交互 (Tactile Precision & Predictable Motion)**  
   所有的动效严格控制在 150ms ~ 300ms，以微位移与透明度变化为主，彻底移除夸张旋转、大幅度果冻弹性回弹与眩晕感，同时完整兼容 `prefers-reduced-motion`。

---

## 2. 色彩系统 (Color Token Palette)

所有的色彩统一收敛为单一真实来源（Single Source of Truth），映射至 `tailwind.config.js` 与 `index.css`。

### 2.1 表面阶梯与中性色 (Surfaces & Neutral Grays)

| Token 命名 | 色值 | 用途与层级说明 |
| :--- | :--- | :--- |
| `bg-surface-0` (`background`) | `#141513` | 应用全局最深底色、窗口背景 |
| `bg-surface-1` (`sidebar`) | `#1C1E1B` | 左侧导航栏、输入框背景 |
| `bg-surface-2` (`card`) | `#242623` | 主卡片、内容容器面板 |
| `bg-surface-3` (`secondary`) | `#292C28` | 次级区块、内嵌配置面板、选中态背景 |
| `border-subtle` (`border`) | `#3B3E38` | 1px 容器边框、分隔线、常规控件描边 |
| `border-hover` | `#4F524A` | 悬停状态下的强化描边 |

### 2.2 文本层级 (Typography Tokens)

| Token 命名 | 色值 | 适用场景 |
| :--- | :--- | :--- |
| `text-primary` (`foreground`) | `#EDEBE5` | 界面主要文本、卡片标题、表单输入文字（暖白灰阶） |
| `text-secondary` (`muted-foreground`) | `#A4A59F` | 次级描述、表单标签、说明文字、图标常规态 |
| `text-tertiary` | `#6F706B` | 占位提示文字 (Placeholder)、禁用态文本、时间元数据 |

### 2.3 品牌与强调色 (Brand & Accent)

| Token 命名 | 色值 | 说明 |
| :--- | :--- | :--- |
| `accent` (`brand`) | `#BAA17E` | 暖调香槟古铜金，用于主操作按钮、激活边框与焦点光圈 |
| `accent-hover` | `#C9B393` | 强调色悬停强化 |
| `accent-active` | `#A88E6B` | 强调色点击下沉 |
| `accent-foreground` | `#171815` | 强调色按钮上的高对比度深色文本 |
| `accent-muted` | `rgba(186, 161, 126, 0.12)` | 强调色淡化背景（选中态芯片、轻高亮背景） |

### 2.4 低饱和功能语义色 (Semantic States)

严格保证 WCAG AA 可读性对比度，不使用荧光霓虹绿/红/黄：

| 语义 | 文本色 | 背景填充色 | 描边色 | 适用场景 |
| :--- | :--- | :--- | :--- | :--- |
| **Success (成功)** | `#88A982` | `#1E251E` | `#334333` | 注册完成、已挂载状态、通过校验 |
| **Warning (警示)** | `#CBA167` | `#262118` | `#4B3D28` | “只能注册一次”提示、Windows 运行提示 |
| **Error (错误)** | `#C96B64` | `#261B1B` | `#4E2E2C` | 表单校验报错、接口异常、密码不匹配 |
| **Info (信息)** | `#7B96A8` | `#192025` | `#2E3C47` | 挂载路径说明、快捷方式指引 |

---

## 3. 版面排版与字体系统 (Typography & Spacing)

### 3.1 字体家族
- **主字体栈**：`"Inter", -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "PingFang SC", "Microsoft YaHei", sans-serif`
- **等宽代码栈**：`"JetBrains Mono", "SF Mono", Consolas, Menlo, Monaco, monospace`（用于 IP、脚本名、盘符）

### 3.2 字阶规范 (Font Scale)
- **页面/大标题**：`22px` - `24px`，`font-semibold`，`leading-tight`，追踪间距微收紧 `-0.015em`
- **卡片副标题**：`15px` - `16px`，`font-medium`
- **表单正文与输入**：`14px`，`font-normal`，`leading-normal`
- **辅助说明与标签**：`12px` - `13px`，`text-secondary`
- **徽章与微型标记**：`11px`，`font-medium`，`font-mono`

### 3.3 间距与网格规范 (Grid & Spacing Scale)
基于 4px 基础网格系统：
- 控件内边距：`8px (p-2)`, `12px (p-3)`, `16px (p-4)`
- 面板内边距：`20px (p-5)`, `24px (p-6)`, `28px (p-7)`
- 布局栅格跨度：`gap-2 (8px)`, `gap-3 (12px)`, `gap-4 (16px)`

### 3.4 圆角体系 (Border Radius)
- **按钮与输入框**：`8px` (`rounded-[8px]`)
- **面板与容器卡片**：`12px` (`rounded-[12px]`)
- **状态芯片与徽章**：`6px` (`rounded-[6px]`)
- **圆形头像/步骤序号**：`9999px` (`rounded-full`)

---

## 4. 核心组件规范 (Component Specifications)

### 4.1 按钮 (Button)
- **Default (主按钮)**：背景 `#BAA17E`，文字 `#171815`，字重 `font-semibold`，圆角 8px。无彩色阴影，仅微弱 1px 内描边或柔和下沉。悬停色 `#C9B393`。
- **Secondary (次级按钮)**：背景 `#292C28`，描边 `#3B3E38`，文字 `#EDEBE5`，悬停背景 `#323530`。
- **Ghost (幽灵按钮)**：背景透明，悬停背景 `rgba(255,255,255,0.04)`，文字 `#A4A59F`，悬停文字 `#EDEBE5`。
- **焦点状态 (Focus State)**：统一 `focus-visible:ring-2 focus-visible:ring-[#BAA17E] focus-visible:ring-offset-2 focus-visible:ring-offset-[#141513]`。

### 4.2 卡片容器 (Card)
- 背景 `#242623`，描边 `1px solid #3B3E38`，圆角 12px。
- 不使用强烈发光阴影，仅使用深黑漫反射阴影（`shadow-[0_12px_32px_rgba(0,0,0,0.45)]`）。

### 4.3 输入框 (Input)
- 高度 `42px`，背景 `#1C1E1B`，描边 `1px solid #3B3E38`，圆角 8px。
- 文本 `#EDEBE5`，占位符 `#6F706B`。
- 聚焦态：边框平滑过渡为 `#BAA17E`，微弱外轮廓光圈 `ring-1 ring-[#BAA17E]/50`。

### 4.4 业务组选择芯片 (Department Selectors)
- 放弃之前的“影视紫”与“写作绿”两套高饱和彩色卡片。
- 采用统一的工业设计语言：
  - **未选中态**：背景 `#1C1E1B`，边框 `#3B3E38`，图标与文字 `#A4A59F`。
  - **选中态**：背景 `#292C28`，边框 `#BAA17E`，文字 `#EDEBE5`，右上方显示精准的金属香槟勾选指示。

---

## 5. GSAP 动效系统规范 (Motion Standards)

动效服务于“信息清晰度”与“操作确认”，坚决杜绝干扰工作的过度动画。

### 5.1 动效参数规范
- **动画时长 (Duration)**：严控在 `180ms` ~ `300ms`（入场最长不超过 350ms）。
- **缓动曲线 (Easing)**：
  - 入场：`power2.out` 或 `power3.out`（平滑收敛减速）。
  - Tab 滑块切换：`power2.out` 或 `power2.inOut`（匀称无回弹）。
  - **严禁**使用 `back.out(2)`、`elastic.out` 等大幅度晃动与果冻弹跳。
  - **严禁**使用旋转入场（如 `rotation: -30`）。
- **位移幅度 (Displacement)**：
  - 微位移入场：`y: 8px` 至 `12px`，绝不使用超过 24px 的巨幅抛入。
  - 缩放入场：`scale: 0.98 -> 1.0`（仅配合渐变透明度，幅度极微小）。

### 5.2 性能与生命周期治理
- **GSAP 上下文清理**：所有 React 组件内的 GSAP 动画必须包裹在 `gsap.context()` 中，并在组件卸载时调用 `ctx.revert()`，彻底消灭残留的 Tweens 与内存泄漏。
- **避免属性冲突**：被 GSAP 操控的属性（`opacity`, `transform`），容器不得同时绑定 Tailwind 的 `transition-all`，防止动画引擎与 CSS 动画引擎争夺样式产生卡顿跳帧。
- **无障碍降级 (prefers-reduced-motion)**：
  检测用户系统动态效果设置。若用户开启减弱动画，自动跳过时间轴直接就位，或将时长缩减为 0。

---

## 6. 不良实践警示与清单 (Do's & Don'ts)

| 场景 | 严格禁止 (Don't) | 推荐标准 (Do) |
| :--- | :--- | :--- |
| **背景设计** | 滥用巨大紫色/蓝色模糊渐变球 (blur-3xl blobs) | 干净统一的纯黑灰阶底色 `#141513` |
| **卡片效果** | 玻璃拟态叠加高发光霓虹边框与彩色外发光 | 实色表面 `#242623` + 精细描边 `#3B3E38` |
| **按钮设计** | 蓝紫渐变背景 + 蓝色高斯模糊彩色投影 | 纯色香槟铜金 `#BAA17E` + 黑色深层轻投影 |
| **业务区分** | 影视组全紫、写作组全绿两套霓虹系统 | 统一中性卡片框架，通过图标、描述与铜金描边标记选中 |
| **入场动效** | 旋转翻滚、大距离缩放、果冻弹性弹跳 (`back.out(2)`) | 8px~12px 微位移 + 透明度淡入 (`power2.out`) |
| **虚假信息** | 硬编码“局域网万兆在线”、“7.3 TB 可用空间”等假监控 | 真实客观陈述“局域网 NAS 服务”、“MuYing 存储总池” |
| **文本交互** | 在全局根元素挂载 `select-none` 导致用户无法复制脚本名 | 允许正文自由框选与复制，仅按钮保持防误选 |
| **未开放功能** | 点击后跳转到假页面或产生误导性视觉交互 | 统一添加明确的“未开放”标记，禁用或保持非可操作状态 |
