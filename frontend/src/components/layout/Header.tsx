import React from 'react';
import { Film, Menu, User, LogOut } from 'lucide-react';
import { Button } from '@/components/ui/button';

interface HeaderProps {
  sidebarOpen: boolean;
  setSidebarOpen: React.Dispatch<React.SetStateAction<boolean>>;
  currentUser?: {
    fullname: string;
    linux_username: string;
    group_type: string;
  } | null;
  onLogout?: () => void;
}

export const Header: React.FC<HeaderProps> = ({ 
  sidebarOpen, 
  setSidebarOpen,
  currentUser,
  onLogout
}) => {
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
          {/* 影视制作专业标识：质感深灰底色 + 暖铜金图标 */}
          <div className="h-8 w-8 rounded-[8px] bg-secondary border border-border flex items-center justify-center text-accent">
            <Film className="h-4 w-4" />
          </div>
          <div className="flex items-baseline">
            <span className="font-semibold text-sm tracking-tight text-foreground">沐影</span>
            <span className="text-xs text-muted ml-2 hidden sm:inline">
              资产门户
            </span>
          </div>
        </div>
      </div>

      {/* 右侧：登录状态或操作入口 */}
      <div className="flex items-center gap-2">
        {currentUser && (
          <div className="flex items-center gap-2">
            <div className="flex items-center gap-1.5 px-2.5 py-1 rounded-[6px] bg-secondary border border-border text-xs">
              <User className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
              <span className="font-medium text-foreground">{currentUser.fullname}</span>
              <span className="text-[10px] text-muted font-mono ml-1 px-1 py-0.2 bg-sidebar rounded">
                {currentUser.group_type === 'film' ? '影视组' : '写作组'}
              </span>
            </div>
            {onLogout && (
              <Button
                variant="ghost"
                size="sm"
                onClick={onLogout}
                className="h-7 text-xs text-muted hover:text-destructive hover:bg-secondary px-2"
                title="退出当前登录"
              >
                <LogOut className="h-3.5 w-3.5 mr-1" aria-hidden="true" />
                <span className="hidden sm:inline">退出</span>
              </Button>
            )}
          </div>
        )}
      </div>
    </header>
  );
};
