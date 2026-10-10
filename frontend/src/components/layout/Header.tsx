import React from 'react';
import { Film, Menu, HardDrive, Wifi } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface HeaderProps {
  sidebarOpen: boolean;
  setSidebarOpen: React.Dispatch<React.SetStateAction<boolean>>;
}

export const Header: React.FC<HeaderProps> = ({ sidebarOpen, setSidebarOpen }) => {
  return (
    // 规格要求：顶部工具栏 56–64px
    <header className="h-[60px] border-b border-white/10 bg-slate-950/80 backdrop-blur-md px-4 flex items-center justify-between sticky top-0 z-30 select-none">
      <div className="flex items-center gap-3">
        <Button
          variant="ghost"
          size="icon"
          onClick={() => setSidebarOpen((prev) => !prev)}
          className="text-slate-400 hover:text-white"
          title={sidebarOpen ? '收起侧边栏 (64px)' : '展开侧边栏 (240px)'}
        >
          <Menu className="h-5 w-5" />
        </Button>

        <div className="flex items-center gap-2.5">
          <div className="h-8 w-8 rounded-lg bg-gradient-to-br from-blue-500 to-indigo-600 flex items-center justify-center text-white shadow-sm shadow-blue-500/30">
            <Film className="h-4 w-4" />
          </div>
          <div>
            <span className="font-bold text-base tracking-tight text-white">MuYing</span>
            <span className="text-xs text-slate-400 ml-1.5 font-medium hidden sm:inline">
              团队资产与服务中心
            </span>
          </div>
        </div>
      </div>

      <div className="flex items-center gap-3 text-xs text-slate-400">
        <div className="flex items-center gap-1.5 bg-slate-900/90 border border-white/10 px-2.5 py-1 rounded-full">
          <span className="h-2 w-2 rounded-full bg-emerald-500 animate-pulse" />
          <HardDrive className="h-3.5 w-3.5 text-slate-400" />
          <span className="font-mono text-slate-300">192.168.1.153</span>
        </div>
        <div className="hidden sm:flex items-center gap-1 bg-slate-900/90 border border-white/10 px-2.5 py-1 rounded-full text-slate-300">
          <Wifi className="h-3.5 w-3.5 text-blue-400" />
          <span>局域网万兆在线</span>
        </div>
      </div>
    </header>
  );
};
