# 🎬 MuYing Assets Hub (Assets_Page)

> **影视传媒团队资产盘注册中心与自动化挂载系统**  
> 一站式解决影视团队局域网 NAS 素材盘的高性能挂载、团队权限隔离与入职自动化配置。

---

## 🌟 核心特性与业务价值

* **一人一号，全名无感对接**：员工只需输入全名（如：张三）与密码，系统自动转换为规范的 Linux/Samba 账户，并在 NAS 上建立专属个人空间。
* **团队权限严格隔离 (POSIX ACL)**：
  * **🎥 影视组**：拥有 `01_影视资源库` 完全读写权限，文学创作库只读浏览。
  * **✍️ 写作组**：拥有 `02_文学创作库` 完全读写权限，影视资源库只读浏览。
  * **📢 公共素材库**：全员读写共享。
  * **👤 个人空间**：按员工姓名自动建立独立私有目录，他人无权访问。
* **单 IP 注册安全策略**：每台设备/每个 IP 仅限注册一次，防止重复误操作与凭据冲突。
* **100% 零管理员凭据泄露**：下发的客户端挂载脚本仅包含员工本人注册生成的身份凭据，绝对不包含任何管理员账密。
* **Windows 客户端一键开箱**：动态下发 `.bat` 脚本，自动写入 Windows 凭据管理器（开机免密直连）、自动挂载为 **Z: 盘（命名为 MuYing）**、自动在桌面生成直达快捷方式并呼出资源管理器。

---

## 🛠️ 技术架构体系

本项目采用**高性能动静分离架构**，前端具备顶级 SaaS 工具级交互质感，后端保持极限轻量（在 NAS 端仅占用 10MB 内存）：

### 🖥️ 前端技术栈 (Frontend)
* **页面开发**：`React 18` + `TypeScript` + `Vite`
* **样式系统**：`Tailwind CSS` (深色电影工业风 / 玻璃拟态)
* **基础组件**：`shadcn/ui` 风格原子组件（Card、Button、Input）
* **动效引擎**：`GSAP 3` (弹性标签滑块与多级错峰电影感入场动画)
* **图标体系**：`Lucide Icons` (高清矢量线条图标，替代传统 Emoji)

#### 📐 严格遵循的设计规范 (Design Tokens)
* **字体系统**：中文系统 UI 字体 + Inter 数字/英文
* **字号层级**：正文 14px (行高约 1.5) / 页面标题 24~28px / 辅助文本 12~13px
* **间距体系**：4 / 8 / 12 / 16 / 24 / 32px (标准 4px 网格)
* **圆角规格**：卡片 12px (`rounded-[12px]`) / 按钮与输入框 8px (`rounded-[8px]`)
* **导航布局**：左侧导航栏展开 240px、收起 64px；顶部工具栏 60px
* **边框样式**：1px 低对比度描边 (`border-white/10`)

### ⚙️ 后端与系统底层 (Backend)
* **Web 服务**：`Python 3.11` 原生标准库多线程 HTTP 服务（零三方 pip 依赖）
* **数据存储**：`SQLite 3`（轻量单文件数据库，记录用户组别、审计日志与 IP 绑定）
* **底层系统**：Debian 12 / Linux 原生用户系统 + Samba (`smbpasswd`)
* **文件系统权限**：Linux POSIX ACL (`setfacl` / `getfacl`)

---

## 📂 仓库目录结构

```text
Assets_Page/
├── frontend/                   # 现代 React + TypeScript + Vite 前端工程
│   ├── src/
│   │   ├── components/
│   │   │   ├── layout/         # 顶部工具栏 (Header) 与侧边栏 (Sidebar)
│   │   │   ├── registration/   # 注册卡片 (RegisterCard) 与成功卡片 (SuccessCard)
│   │   │   └── ui/             # 原子 UI 组件 (Button 8px, Card 12px, Input)
│   │   ├── lib/
│   │   │   └── utils.ts        # cn() 样式工具函数
│   │   ├── App.tsx             # 布局与模块集成总入口
│   │   ├── index.css           # Tailwind 样式与暗色变量
│   │   └── main.tsx            # React 挂载入口
│   ├── index.html              # 模板入口
│   ├── package.json
│   ├── tsconfig.json
│   ├── vite.config.ts
│   └── tailwind.config.js
│
├── backend/                    # Python 3.11 轻量原生后端 & NAS 运维核心
│   ├── core/
│   │   ├── nas_manager.py      # Linux & Samba 用户自动开户、组分配、个人空间管理
│   │   ├── pinyin_dict.py      # 中文全名转系统安全 ASCII 账号
│   │   └── script_generator.py # 客户端个性化 .bat 自动化挂载脚本生成
│   ├── config.py               # 服务与存储池配置 (NAS IP, 盘符, 路径)
│   ├── database.py             # SQLite 数据库管理与单 IP 限额校验
│   ├── server.py               # 生产 HTTP 服务端 (托管前端 dist 产物与 API)
│   └── muying-hub.service      # systemd 自启守护进程配置模板
│
├── .gitignore
└── README.md
```

---

## 🚀 开发与部署指南

### 1. 前端本地开发与构建

```bash
cd frontend

# 安装依赖
npm install

# 启动本地热重载调试 (默认端口 5173，自动代理 API 到 NAS)
npm run dev

# 编译为生产静态包 (输出至 dist/)
npm run build
```

### 2. 部署到 NAS (绿联 UGOS Pro / Debian)

1. 将前端编译生成的 `dist/` 目录以及 `backend/` 下的所有文件上传至 NAS 的存储目录（例如 `/volume1/muying_hub`）。
2. 配置服务自启动守护：
   ```bash
   sudo cp /volume1/muying_hub/muying-hub.service /etc/systemd/system/
   sudo systemctl daemon-reload
   sudo systemctl enable muying-hub.service
   sudo systemctl restart muying-hub.service
   ```
3. 检查服务运行状态：
   ```bash
   sudo systemctl status muying-hub.service
   ```

---

## 🔒 安全说明

1. 本系统通过限制一个 IP 仅允许注册一次，有效避免局域网滥用与凭据覆盖；
2. 员工下载的挂载脚本内部仅加密写入自己的 Windows 凭据，不会暴露任何团队超级管理员密码；
3. Samba 共享严格遵循 Linux 底层 POSIX ACL，即使网络可达，非本组成员也无法越权修改文件。
