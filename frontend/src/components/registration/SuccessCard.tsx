import React, { useEffect, useState, useRef } from 'react';
import gsap from 'gsap';
import { 
  CheckCircle2, 
  Download, 
  ArrowLeft, 
  Video,
  PenLine,
  Info,
  ShieldCheck
} from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

interface SuccessCardProps {
  fullname: string;
  linuxUsername?: string;
  onBack: () => void;
}

export const SuccessCard: React.FC<SuccessCardProps> = ({ fullname, linuxUsername, onBack }) => {
  const [userInfo, setUserInfo] = useState<{
    display_name: string;
    linux_username: string;
    group_type: string;
  } | null>(null);

  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // 异步拉取详细用户信息
    fetch(`/api/user-info?name=${encodeURIComponent(fullname)}`)
      .then((res) => res.json())
      .then((data) => {
        if (data.success && data.user) {
          setUserInfo(data.user);
        }
      })
      .catch((err) => console.error(err));
  }, [fullname]);

  // GSAP 规范：克制、精准入场序列（时长 200–300ms，杜绝夸张旋转与强烈回弹）
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
        '.gsap-success-item',
        { opacity: 0, y: 6 },
        { opacity: 1, y: 0, duration: 0.2, stagger: 0.04 },
        '-=0.15'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  const activeLinuxUser = userInfo?.linux_username || linuxUsername || '';
  const downloadUrl = `/api/download-script?name=${encodeURIComponent(activeLinuxUser || fullname)}`;
  const filename = activeLinuxUser
    ? `Mount_MuYing_${activeLinuxUser}.bat`
    : `Mount_MuYing.bat`;

  return (
    <div ref={containerRef} className="w-full max-w-[520px]">
      <Card className="border border-border bg-card shadow-panel">
        <CardHeader className="text-center pb-3 pt-7">
          <div className="flex justify-center mb-2.5">
            <div className="h-11 w-11 rounded-full bg-success-bg border border-success-border flex items-center justify-center text-success">
              <CheckCircle2 className="h-5 w-5" aria-hidden="true" />
            </div>
          </div>
          <CardTitle className="text-xl font-semibold text-foreground tracking-tight">
            账号已就绪，请下载挂载工具
          </CardTitle>
          <CardDescription className="text-muted text-xs mt-1">
            下载并在您的 Windows 电脑上运行原生挂载工具
          </CardDescription>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 原生安全架构提示：Windows 原生凭据托管，不再需要临时 Token */}
          <div className="gsap-success-item p-2.5 rounded-[8px] bg-secondary border border-border text-xs text-foreground flex items-center gap-2.5">
            <ShieldCheck className="h-4 w-4 shrink-0 text-accent" aria-hidden="true" />
            <span className="font-medium text-xs">
              Windows 原生凭据保护：首次双击输入一次密码，后续开机自动持久化免密连接
            </span>
          </div>

          {/* 员工参数规格框：暗室微明度次级表面 */}
          <div className="gsap-success-item p-3.5 rounded-[8px] bg-sidebar border border-border text-xs space-y-2">
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">员工姓名</span>
              <span className="font-medium text-foreground">{userInfo?.display_name || fullname}</span>
            </div>
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">所属团队</span>
              <span className="font-medium text-foreground flex items-center gap-1.5">
                {userInfo?.group_type === 'film' ? (
                  <>
                    <Video className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
                    <span>影视组</span>
                  </>
                ) : (
                  <>
                    <PenLine className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
                    <span>写作组</span>
                  </>
                )}
              </span>
            </div>
            {activeLinuxUser && (
              <div className="flex justify-between items-center py-1 border-b border-border/50">
                <span className="text-muted">存储账号</span>
                <span className="font-mono text-accent">{activeLinuxUser}</span>
              </div>
            )}
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">自动挂载盘符</span>
              <span className="font-mono text-foreground">本地 Z: 盘 (名为 沐影)</span>
            </div>
            <div className="flex justify-between items-center py-1">
              <span className="text-muted">直达快捷方式</span>
              <span className="font-mono text-foreground">桌面 [沐影] 快捷方式</span>
            </div>
          </div>

          {/* 下载主操作按钮：暖铜金高对比度按键 */}
          <div className="gsap-success-item pt-1">
            <a
              href={downloadUrl}
              download={filename}
              className="w-full inline-block"
            >
              <Button
                className="w-full h-11 text-xs font-semibold inline-flex items-center justify-center gap-2"
              >
                <Download className="h-4 w-4" aria-hidden="true" />
                <span>立即下载挂载工具 ({filename})</span>
              </Button>
            </a>
          </div>

          {/* 挂载使用指南 */}
          <div className="gsap-success-item p-3.5 rounded-[8px] bg-sidebar border border-border text-xs space-y-3">
            <div className="font-medium text-foreground flex items-center gap-1.5">
              <Info className="h-4 w-4 text-accent" aria-hidden="true" />
              <span>使用步骤指引</span>
            </div>

            <div className="flex gap-2.5 items-start">
              <span className="h-4 w-4 rounded-full bg-secondary border border-border flex items-center justify-center text-[10px] text-muted font-mono shrink-0 mt-0.5">
                1
              </span>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">下载挂载工具</div>
                <div className="text-muted leading-relaxed">
                  保存 <code className="text-accent font-mono bg-secondary px-1 py-0.5 rounded text-[11px]">{filename}</code> 到本地电脑（通常位于“下载”文件夹）。
                </div>
              </div>
            </div>

            <div className="flex gap-2.5 items-start">
              <span className="h-4 w-4 rounded-full bg-secondary border border-border flex items-center justify-center text-[10px] text-muted font-mono shrink-0 mt-0.5">
                2
              </span>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">双击运行并输入密码</div>
                <div className="text-muted leading-relaxed">
                  双击运行该工具，Windows 将弹出系统原生凭据窗口，输入您在门户注册的密码即可。
                </div>
              </div>
            </div>

            <div className="flex gap-2.5 items-start">
              <span className="h-4 w-4 rounded-full bg-secondary border border-border flex items-center justify-center text-[10px] text-muted font-mono shrink-0 mt-0.5">
                3
              </span>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">自动就绪与开机免密</div>
                <div className="text-muted leading-relaxed">
                  工具将自动建立 <strong className="text-foreground">Z: 盘</strong> 与桌面快捷方式，凭据由 Windows 原生安全管理器加密保护，开机自动免密连接。
                </div>
              </div>
            </div>
          </div>

          <div className="text-center pt-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={onBack}
              className="text-muted hover:text-foreground inline-flex items-center gap-1.5 text-xs"
            >
              <ArrowLeft className="h-3.5 w-3.5" aria-hidden="true" />
              <span>返回门户首页</span>
            </Button>
          </div>
        </div>
      </Card>
    </div>
  );
};
