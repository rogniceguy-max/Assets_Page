import React from 'react';
import { 
  Compass, 
  Video, 
  PenTool, 
  FolderTree, 
  Users, 
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
      available: true,
      badge: '当前',
    },
    {
      id: 'film',
      label: '影视资源库',
      icon: Video,
      available: false,
      badge: '规划中',
    },
    {
      id: 'writing',
      label: '文学创作库',
      icon: PenTool,
      available: false,
      badge: '规划中',
    },
    {
      id: 'assets',
      label: '公共素材空间',
      icon: FolderTree,
      available: false,
      badge: '规划中',
    },
    {
      id: 'members',
      label: '成员权限管理',
      icon: Users,
      available: false,
      badge: '规划中',
    },
  ];

  return (
    // 规格要求：左侧导航栏 展开 240px，收起 64px；专业桌面暗室层级
    <aside
      className={cn(
        'border-r border-border bg-sidebar transition-all duration-200 ease-out flex flex-col justify-between shrink-0 z-20',
        open ? 'w-[240px]' : 'w-[64px]'
      )}
    >
      <div className="p-2.5 space-y-1">
        {menuItems.map((item) => {
          const Icon = item.icon;
          const isSelected = activeNav === item.id;

          if (!item.available) {
            // 未开放模块：明确呈现不可用/开发中状态，避免可操作却无功能的伪入口
            return (
              <div
                key={item.id}
                className={cn(
                  'w-full flex items-center gap-3 px-3 py-2 rounded-[8px] text-xs text-muted-foreground opacity-60 cursor-not-allowed select-none relative',
                  !open && 'justify-center px-0'
                )}
                title={!open ? `${item.label} (规划中)` : undefined}
              >
                <Icon className="h-4 w-4 shrink-0 text-muted-foreground" />
                {open && (
                  <div className="flex items-center justify-between flex-1 overflow-hidden">
                    <span className="truncate">{item.label}</span>
                    <span className="text-[10px] px-1.5 py-0.5 rounded-[4px] bg-secondary text-muted-foreground font-mono">
                      {item.badge}
                    </span>
                  </div>
                )}
              </div>
            );
          }

          return (
            <button
              key={item.id}
              onClick={() => setActiveNav(item.id)}
              className={cn(
                'w-full flex items-center gap-3 px-3 py-2.5 rounded-[8px] text-xs transition-colors duration-150 text-left relative group',
                !open && 'justify-center px-0',
                isSelected
                  ? 'bg-secondary text-foreground font-medium border border-border'
                  : 'text-muted hover:text-foreground hover:bg-secondary/40 border border-transparent'
              )}
              title={!open ? item.label : undefined}
            >
              <Icon
                className={cn(
                  'h-4 w-4 shrink-0 transition-colors',
                  isSelected ? 'text-accent' : 'text-muted group-hover:text-foreground'
                )}
              />

              {open && (
                <div className="flex items-center justify-between flex-1 overflow-hidden">
                  <span className="truncate">{item.label}</span>
                  <span className="text-[10px] px-1.5 py-0.5 rounded-[4px] bg-accent/15 text-accent font-medium">
                    {item.badge}
                  </span>
                </div>
              )}
            </button>
          );
        })}
      </div>

      <div className="p-2.5 border-t border-border space-y-2">
        {open ? (
          <div className="bg-secondary/70 border border-border p-2.5 rounded-[8px] text-xs text-muted flex items-center gap-2.5">
            <HardDrive className="h-4 w-4 text-accent shrink-0" />
            <div className="overflow-hidden">
              <div className="text-foreground text-xs font-medium truncate">MuYing 存储总池</div>
              <div className="text-[11px] text-muted-foreground">局域网共享卷已就绪</div>
            </div>
          </div>
        ) : null}

        <button
          onClick={() => setOpen((prev) => !prev)}
          className="w-full flex items-center justify-center p-2 rounded-[8px] text-muted hover:text-foreground hover:bg-secondary/50 transition-colors"
          title={open ? '收起导航栏' : '展开导航栏'}
          aria-label={open ? '收起导航栏' : '展开导航栏'}
        >
          {open ? (
            <span className="text-xs text-muted">收起侧栏</span>
          ) : (
            <ChevronRight className="h-3.5 w-3.5" />
          )}
        </button>
      </div>
    </aside>
  );
};
