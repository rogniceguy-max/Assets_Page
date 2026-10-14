# WPF 项目表格模块 v3.1

合并步骤、服务注入、账号隔离、主题和实现边界见 [INTEGRATION.md](INTEGRATION.md)。优先契约见 [WORKFLOW_V3.md](../contracts/WORKFLOW_V3.md)。

入口 ProjectWorkbooksView.xaml 与 ProjectWorkbooksViewModel；Contracts.cs、HttpProjectWorkbookService.cs、WorkbookTransport.cs 提供 DTO、接口与有界流式传输。新增项目/修订、节拍规划讨论、原批注/来源、素材检索对比、处理状态、选定历史与当前导出调用。

独立源码包未修改正在重构的原项目，没有编译或运行。真实 NAS、权限、xlsx 解析/模板/当前导出引擎仍须宿主接线。

节拍故事与规划默认全文显示，点击行或按 Enter / Space 进入批注详情。详情首屏显示原批注，下方编辑规划，底部提供讨论与来源。首页按钮为“新建节拍表”和“新建资产表”；没有常驻刷新按钮，只有失败后出现“重新加载”，保留草稿与版本冲突保护。
