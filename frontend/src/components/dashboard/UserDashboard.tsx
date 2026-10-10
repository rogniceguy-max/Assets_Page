import React, { useEffect, useRef } from 'react';
import gsap from 'gsap';
import { 
  User, 
  HardDrive, 
  Download, 
  RefreshCw, 
  LogOut, 
  Video, 
  PenLine, 
  FolderTree,
  CheckCircle2,
  ExternalLink
} from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

export interface UserDashboardProps {
  user: {
    fullname: string;
    linux_username: string;
    group_type: string;
    token?: string;
  };
  onMount: () => void;
  onLogout: () => void;
}

export const UserDashboard: React.FC<UserDashboardProps> = ({
  user,
  onMount,
  onLogout,
}) => {
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!containerRef.current) return;
    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (prefersReducedMotion) return;

    const ctx = gsap.context(() => {
      const tl = gsap.timeline({ defaults: { ease: 'power2.out' } });
      tl.fromTo(
        containerRef.current,
        { opacity: 0, y: 12 },
        { opacity: 1, y: 0, duration: 0.28 }
      );
      tl.fromTo(
        '.gsap-dash-item',
        { opacity: 0, y: 6 },
        { opacity: 1, y: 0, duration: 0.2, stagger: 0.04 },
        '-=0.15'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  const isFilm = user.group_type === 'film';

  return (
    <div ref={containerRef} className="w-full max-w-[520px]">
      <Card className="border border-border bg-card shadow-panel">
        <CardHeader className="text-center pb-4 pt-7">
          <div className="flex justify-center mb-2.5">
            <div className="h-10 w-10 rounded-[8px] bg-secondary border border-border flex items-center justify-center text-accent">
              {isFilm ? (
                <Video className="h-5 w-5" aria-hidden="true" />
              ) : (
                <PenLine className="h-5 w-5" aria-hidden="true" />
              )}
            </div>
          </div>
          <CardTitle className="text-xl font-semibold text-foreground tracking-tight">
            欢迎回来，{user.fullname}
          </CardTitle>
          <CardDescription className="text-xs text-muted mt-1">
            MuYing 团队资产与挂载工作台
          </CardDescription>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 状态指示框：明确区分网页登录与本地挂载状态 */}
          <div className="gsap-dash-item p-3 rounded-[8px] bg-secondary border border-border text-xs space-y-2">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-2">
                <span className="h-2 w-2 rounded-full bg-success inline-block" />
                <span className="font-medium text-foreground">网页工作台已登录 (账号已授权)</span>
              </div>
              <span className="text-[11px] text-muted font-mono">192.168.1.153</span>
            </div>
            <div className="flex items-center justify-between pt-1.5 border-t border-border/50 text-[11px]">
              <span className="text-muted">本地 Z: 盘状态</span>
              <span className="text-accent font-medium">需运行挂载工具建立映射</span>
            </div>
          </div>

          {/* 员工账户参数框 */}
          <div className="gsap-dash-item p-3.5 rounded-[8px] bg-sidebar border border-border text-xs space-y-2">
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">员工姓名</span>
              <span className="font-medium text-foreground">{user.fullname}</span>
            </div>
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">Linux 存储账号</span>
              <span className="font-mono text-accent">{user.linux_username}</span>
            </div>
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">所属业务组</span>
              <span className="font-medium text-foreground">
                {isFilm ? '影视制作组' : '文学创作组'}
              </span>
            </div>
            <div className="flex justify-between items-center py-1">
              <span className="text-muted">本地分配盘符</span>
              <span className="font-mono text-foreground font-semibold">Z: 盘</span>
            </div>
          </div>

          {/* 挂载操作按钮 */}
          <div className="gsap-dash-item space-y-2 pt-1">
            <Button
              onClick={onMount}
              className="w-full h-10 text-xs font-semibold inline-flex items-center justify-center gap-2"
            >
              <Download className="h-4 w-4" aria-hidden="true" />
              <span>下载 Windows 挂载工具 (Z: 盘)</span>
            </Button>
            <Button
              variant="outline"
              onClick={onMount}
              className="w-full h-9 text-xs font-medium inline-flex items-center justify-center gap-2 border-border text-muted hover:text-foreground hover:bg-secondary"
            >
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
              <span>重新挂载 / 恢复网络盘连接</span>
            </Button>
          </div>

          {/* 团队共享目录路径说明 */}
          <div className="gsap-dash-item p-3 rounded-[8px] bg-sidebar border border-border text-xs space-y-1.5">
            <div className="font-medium text-foreground flex items-center gap-1.5">
              <FolderTree className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
              <span>已授权资产目录</span>
            </div>
            <div className="text-[11px] text-muted space-y-1 font-mono">
              <div className="flex items-center justify-between">
                <span>Z:\00_公共素材</span>
                <span className="text-[10px] text-success">全员读写</span>
              </div>
              <div className="flex items-center justify-between">
                <span>{isFilm ? 'Z:\\01_影视资源库' : 'Z:\\02_文学创作库'}</span>
                <span className="text-[10px] text-accent">所属组读写</span>
              </div>
              <div className="flex items-center justify-between">
                <span>{isFilm ? 'Z:\\02_文学创作库' : 'Z:\\01_影视资源库'}</span>
                <span className="text-[10px] text-muted">跨组只读</span>
              </div>
              <div className="flex items-center justify-between">
                <span>{`Z:\\03_个人空间\\${user.fullname}`}</span>
                <span className="text-[10px] text-accent">个人独占</span>
              </div>
            </div>
          </div>

          {/* 退出登录 */}
          <div className="text-center pt-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={onLogout}
              className="text-muted hover:text-destructive inline-flex items-center gap-1.5 text-xs"
            >
              <LogOut className="h-3.5 w-3.5" aria-hidden="true" />
              <span>退出当前账号</span>
            </Button>
          </div>
        </div>
      </Card>
    </div>
  );
};
