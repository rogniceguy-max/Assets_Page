import React from 'react';
import { Film, Menu, HardDrive, Server } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface HeaderProps {
  sidebarOpen: boolean;
  setSidebarOpen: React.Dispatch<React.SetStateAction<boolean>>;
}

export const Header: React.FC<HeaderProps> = ({ sidebarOpen, setSidebarOpen }) => {
  return (
    // 规格要求：顶部工具栏 56–64px，专业桌面软件质感
    <header className="h-[60px] border-b border-border bg-sidebar px-4 flex items-center justify-between sticky top-0 z-30">
      <div className="flex items-center gap-3">
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setSidebarOpen((prev) => !prev)}
          className="text-muted hover:text-foreground"
          title={sidebarOpen ? '收起侧边栏' : '展开侧边栏'}
          aria-label={sidebarOpen ? '收起侧边栏' : '展开侧边栏'}
        >
          <Menu className="h-4 w-4" />
        </Button>

        <div className="flex items-center gap-2.5">
          {/* 影视制作专业标识：质感深灰底色 + 暖铜金图标，移除浮夸蓝紫渐变与光晕 */}
          <div className="h-8 w-8 rounded-[8px] bg-secondary border border-border flex items-center justify-center text-accent">
            <Film className="h-4 w-4" />
          </div>
          <div className="flex items-baseline">
            <span className="font-semibold text-sm tracking-tight text-foreground">MuYing</span>
            <span className="text-xs text-muted ml-2 hidden sm:inline">
              资产与挂载门户
            </span>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-2.5 text-xs text-muted">
        {/* 客观 NAS 服务标识：移除虚假“万兆在线”字样，客观展示真实 IP 与服务状态 */}
        <div className="flex items-center gap-2 bg-secondary border border-border px-3 py-1.5 rounded-[6px]">
          <span className="h-1.5 w-1.5 rounded-full bg-success" />
          <Server className="h-3.5 w-3.5 text-muted" />
          <span className="font-mono text-xs text-foreground">192.168.1.153</span>
        </div>
        <div className="hidden sm:flex items-center gap-1.5 bg-secondary border border-border px-3 py-1.5 rounded-[6px] text-muted">
          <HardDrive className="h-3.5 w-3.5 text-muted" />
          <span>Samba 存储服务</span>
        </div>
      </div>
    </header>
  );
};
