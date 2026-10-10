import React from 'react';
import { 
  Compass, 
  Video, 
  PenTool, 
  FolderTree, 
  Users, 
  ChevronLeft, 
  ChevronRight,
  HardDrive
} from 'lucide-react';
import { cn } from '@/lib/utils';

interface SidebarProps {
  open: boolean;
  setOpen: React.Dispatch<React.SetStateAction<boolean>>;
  activeNav: string;
  setActiveNav: (nav: string) => void;
}

export const Sidebar: React.FC<SidebarProps> = ({ 
  open, 
  setOpen, 
  activeNav, 
  setActiveNav 
}) => {
  const menuItems = [
    {
      id: 'onboarding',
      label: '挂载与入职',
      icon: Compass,
      active: true,
      badge: '当前',
    },
    {
      id: 'film',
      label: '影视资源库',
      icon: Video,
      active: false,
      badge: '即将开放',
    },
    {
      id: 'writing',
      label: '文学创作库',
      icon: PenTool,
      active: false,
      badge: '即将开放',
    },
    {
      id: 'assets',
      label: '公共素材空间',
      icon: FolderTree,
      active: false,
      badge: '即将开放',
    },
    {
      id: 'members',
      label: '成员权限管理',
      icon: Users,
      active: false,
      badge: '即将开放',
    },
  ];

  return (
    // 规格要求：左侧导航栏 展开 240px，收起 64px
    <aside
      className={cn(
        'border-r border-white/10 bg-slate-950/60 backdrop-blur-md transition-all duration-300 ease-in-out flex flex-col justify-between shrink-0 select-none z-20',
        open ? 'w-[240px]' : 'w-[64px]'
      )}
    >
      <div className="p-3 space-y-1">
        {menuItems.map((item) => {
          const Icon = item.icon;
          const isSelected = activeNav === item.id;

          return (
            <button
              key={item.id}
              onClick={() => setActiveNav(item.id)}
              className={cn(
                'w-full flex items-center gap-3 px-3 py-2.5 rounded-[8px] text-sm transition-all text-left relative group',
                isSelected
                  ? 'bg-blue-600/15 text-white font-medium border border-blue-500/30'
                  : 'text-slate-400 hover:text-white hover:bg-white/5 border border-transparent'
              )}
              title={!open ? item.label : undefined}
            >
              <Icon
                className={cn(
                  'h-5 w-5 shrink-0 transition-colors',
                  isSelected ? 'text-blue-400' : 'text-slate-400 group-hover:text-white'
                )}
              />

              {open && (
                <div className="flex items-center justify-between flex-1 overflow-hidden">
                  <span className="truncate">{item.label}</span>
                  {item.badge && (
                    <span
                      className={cn(
                        'text-[10px] px-1.5 py-0.5 rounded font-mono',
                        item.active
                          ? 'bg-blue-500/20 text-blue-300 border border-blue-500/30'
                          : 'bg-slate-800 text-slate-500'
                      )}
                    >
                      {item.badge}
                    </span>
                  )}
                </div>
              )}
            </button>
          );
        })}
      </div>

      <div className="p-3 border-t border-white/10 space-y-2">
        {open ? (
          <div className="bg-slate-900/60 border border-white/5 p-2.5 rounded-[8px] text-xs text-slate-400 flex items-center gap-2">
            <HardDrive className="h-4 w-4 text-emerald-400 shrink-0" />
            <div className="overflow-hidden">
              <div className="text-slate-200 font-medium truncate">MuYing 资产池</div>
              <div className="text-[11px] text-slate-500">7.3 TB 可用空间</div>
            </div>
          </div>
        ) : null}

        <button
          onClick={() => setOpen((prev) => !prev)}
          className="w-full flex items-center justify-center p-2 rounded-[8px] text-slate-400 hover:text-white hover:bg-white/5 transition-colors"
          title={open ? '收起导航栏' : '展开导航栏'}
        >
          {open ? (
            <div className="flex items-center gap-1.5 text-xs text-slate-400">
              <ChevronLeft className="h-4 w-4" />
              <span>收起侧栏 (64px)</span>
            </div>
          ) : (
            <ChevronRight className="h-4 w-4" />
          )}
        </button>
      </div>
    </aside>
  );
};
