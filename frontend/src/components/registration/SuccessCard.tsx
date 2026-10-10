import React, { useEffect, useState, useRef } from 'react';
import gsap from 'gsap';
import { 
  CheckCircle2, 
  Download, 
  ArrowLeft, 
  Video,
  PenLine,
  Info,
  Clock
} from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

interface SuccessCardProps {
  fullname: string;
  token: string;
  onBack: () => void;
}

export const SuccessCard: React.FC<SuccessCardProps> = ({ fullname, token, onBack }) => {
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

  // GSAP 规范：克制、精准入场序列（平滑减速，时长严控在 200–300ms，杜绝夸张旋转与强烈回弹）
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

  const downloadUrl = `/api/download-script?token=${encodeURIComponent(token)}`;
  const filename = userInfo
    ? `Mount_MuYing_${userInfo.linux_username}.bat`
    : `Mount_MuYing_${fullname}.bat`;

  return (
    <div ref={containerRef} className="w-full max-w-[520px]">
      <Card className="border border-border bg-card shadow-panel">
        <CardHeader className="text-center pb-3 pt-7">
          <div className="flex justify-center mb-2.5">
            {/* 成功标识：低饱和语义绿底色与精细描边，移除夸张旋转与发光 */}
            <div className="h-11 w-11 rounded-full bg-success-bg border border-success-border flex items-center justify-center text-success">
              <CheckCircle2 className="h-5 w-5" aria-hidden="true" />
            </div>
          </div>
          <CardTitle className="text-xl font-semibold text-foreground tracking-tight">
            账号注册完成，专属脚本已生成
          </CardTitle>
          <CardDescription className="text-muted text-xs mt-1">
            请下载专属配置文件，并在您的电脑上执行一键配置
          </CardDescription>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 60秒一次性安全挂载时效提示 */}
          <div className="gsap-success-item p-2.5 rounded-[8px] bg-warning-bg border border-warning-border text-xs text-warning flex items-center gap-2">
            <Clock className="h-4 w-4 shrink-0 text-warning" aria-hidden="true" />
            <span className="font-medium">安全时效提示：专属脚本的一次性挂载窗口为 60 秒，下载后请尽快运行</span>
          </div>

          {/* 员工参数规格框：暗室微明度次级表面 */}
          <div className="gsap-success-item p-3.5 rounded-[8px] bg-sidebar border border-border text-xs space-y-2">
            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">员工姓名</span>
              <span className="font-semibold text-foreground">{fullname}</span>
            </div>

            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">所属团队</span>
              <div className="flex items-center gap-1.5 font-medium text-foreground">
                {userInfo?.group_type === 'writing' ? (
                  <span className="flex items-center gap-1 text-xs">
                    <PenLine className="h-3.5 w-3.5 text-accent" aria-hidden="true" /> 写作组
                  </span>
                ) : (
                  <span className="flex items-center gap-1 text-xs">
                    <Video className="h-3.5 w-3.5 text-accent" aria-hidden="true" /> 影视组
                  </span>
                )}
              </div>
            </div>

            <div className="flex justify-between items-center py-1 border-b border-border/50">
              <span className="text-muted">自动挂载盘符</span>
              <span className="font-mono text-xs text-foreground">本地 Z: 盘 (名为 MuYing)</span>
            </div>

            <div className="flex justify-between items-center py-1">
              <span className="text-muted">直达快捷方式</span>
              <span className="text-foreground">桌面 [MuYing] 文件夹</span>
            </div>
          </div>

          {/* 下载主操作按钮：香槟铜金，单一强焦点，无彩色投影，键盘焦点环可见 */}
          <a
            href={downloadUrl}
            className="gsap-success-item flex items-center justify-center gap-2 w-full h-11 rounded-[8px] bg-accent text-accent-foreground font-semibold text-xs hover:bg-accent-hover active:bg-accent-active transition-colors select-none focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-background"
          >
            <Download className="h-4 w-4" aria-hidden="true" />
            <span>立即下载挂载脚本 ({filename})</span>
          </a>

          {/* 3 步使用指引：专业排版与信息层级 */}
          <div className="gsap-success-item p-3.5 rounded-[8px] bg-sidebar border border-border space-y-3 text-left text-xs">
            <div className="flex items-center gap-2 font-medium text-foreground text-xs">
              <Info className="h-4 w-4 text-muted" aria-hidden="true" />
              <span>挂载脚本使用指引</span>
            </div>

            <div className="flex items-start gap-2.5">
              <div className="h-4 w-4 rounded-full bg-secondary border border-border text-foreground flex items-center justify-center shrink-0 font-mono text-[10px] mt-0.5">
                1
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">下载专属脚本</div>
                <div className="text-muted leading-relaxed">
                  保存 <code className="font-mono text-foreground">{filename}</code> 至您的本地电脑（通常在“下载”文件夹）。
                </div>
              </div>
            </div>

            <div className="flex items-start gap-2.5">
              <div className="h-4 w-4 rounded-full bg-secondary border border-border text-foreground flex items-center justify-center shrink-0 font-mono text-[10px] mt-0.5">
                2
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">双击运行该脚本</div>
                <div className="text-muted leading-relaxed">
                  脚本将在 60 秒安全窗口内动态获取凭据并自动完成配置。若 Windows 弹出防护提示，点击 <strong className="text-foreground">【更多信息】</strong> → <strong className="text-foreground">【仍要运行】</strong> 即可。
                </div>
              </div>
            </div>

            <div className="flex items-start gap-2.5">
              <div className="h-4 w-4 rounded-full bg-secondary border border-border text-foreground flex items-center justify-center shrink-0 font-mono text-[10px] mt-0.5">
                3
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-foreground">自动就绪与开机免密</div>
                <div className="text-muted leading-relaxed">
                  脚本将自动建立 <strong className="text-foreground">Z: 盘</strong> 与桌面快捷方式，凭据安全加密注入 Windows 凭据管理器，开机永久免输密码。
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
