import React, { useEffect, useState, useRef } from 'react';
import gsap from 'gsap';
import { 
  CheckCircle2, 
  Download, 
  ArrowLeft, 
  Monitor, 
  FolderCheck,
  Video,
  PenLine,
  Info
} from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';

interface SuccessCardProps {
  fullname: string;
  onBack: () => void;
}

export const SuccessCard: React.FC<SuccessCardProps> = ({ fullname, onBack }) => {
  const [userInfo, setUserInfo] = useState<{
    display_name: string;
    linux_username: string;
    group_type: string;
  } | null>(null);

  const containerRef = useRef<HTMLDivElement>(null);
  const badgeRef = useRef<HTMLDivElement>(null);
  const titleRef = useRef<HTMLHeadingElement>(null);
  const metaRef = useRef<HTMLDivElement>(null);
  const downloadBtnRef = useRef<HTMLAnchorElement>(null);
  const tutorialRef = useRef<HTMLDivElement>(null);

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

  // 🌟 GSAP 方案 3：入职开箱仪式感入场序列
  useEffect(() => {
    if (!containerRef.current) return;

    const ctx = gsap.context(() => {
      const tl = gsap.timeline();

      tl.fromTo(
        containerRef.current,
        { opacity: 0, scale: 0.96, y: 20 },
        { opacity: 1, scale: 1, y: 0, duration: 0.55, ease: 'power3.out' }
      );

      tl.fromTo(
        badgeRef.current,
        { scale: 0, rotation: -30 },
        { scale: 1, rotation: 0, duration: 0.55, ease: 'back.out(2.2)' },
        '-=0.35'
      );

      tl.fromTo(
        titleRef.current,
        { opacity: 0, y: 12 },
        { opacity: 1, y: 0, duration: 0.35, ease: 'power2.out' },
        '-=0.25'
      );

      tl.fromTo(
        metaRef.current,
        { opacity: 0, y: 14 },
        { opacity: 1, y: 0, duration: 0.4, ease: 'power2.out' },
        '-=0.2'
      );

      tl.fromTo(
        downloadBtnRef.current,
        { opacity: 0, scale: 0.95 },
        { opacity: 1, scale: 1, duration: 0.4, ease: 'back.out(1.5)' },
        '-=0.15'
      );

      tl.fromTo(
        tutorialRef.current,
        { opacity: 0, y: 16 },
        { opacity: 1, y: 0, duration: 0.4, ease: 'power2.out' },
        '-=0.1'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  const downloadUrl = `/api/download-script?name=${encodeURIComponent(fullname)}`;
  const filename = userInfo
    ? `Mount_MuYing_${userInfo.linux_username}.bat`
    : `Mount_MuYing_${fullname}.bat`;

  return (
    <div ref={containerRef} className="w-full max-w-[560px]">
      <Card className="border border-white/10 shadow-2xl bg-slate-900/80 backdrop-blur-xl">
        <CardHeader className="text-center pb-4 pt-8">
          <div className="flex justify-center mb-3">
            <div
              ref={badgeRef}
              className="h-14 w-14 rounded-full bg-emerald-500/15 border-2 border-emerald-500 flex items-center justify-center text-emerald-400 shadow-lg shadow-emerald-500/25"
            >
              <CheckCircle2 className="h-7 w-7" />
            </div>
          </div>
          <CardTitle ref={titleRef} className="text-2xl font-bold text-white tracking-tight">
            账号注册完成，专属脚本已生成
          </CardTitle>
          <CardDescription className="text-slate-400 text-xs mt-1">
            请下载专属配置文件，并在您的电脑上执行一键配置
          </CardDescription>
        </CardHeader>

        <div className="px-7 pb-8 space-y-5">
          {/* 员工参数规格框 */}
          <div
            ref={metaRef}
            className="p-4 rounded-[12px] bg-slate-950/60 border border-white/10 text-xs space-y-2.5"
          >
            <div className="flex justify-between items-center py-1 border-b border-white/5">
              <span className="text-slate-400">员工全名</span>
              <span className="font-semibold text-sky-400 text-sm">{fullname}</span>
            </div>

            <div className="flex justify-between items-center py-1 border-b border-white/5">
              <span className="text-slate-400">所属团队</span>
              <div className="flex items-center gap-1.5 font-medium">
                {userInfo?.group_type === 'writing' ? (
                  <span className="text-emerald-400 flex items-center gap-1">
                    <PenLine className="h-3.5 w-3.5" /> 写作组
                  </span>
                ) : (
                  <span className="text-purple-400 flex items-center gap-1">
                    <Video className="h-3.5 w-3.5" /> 影视组
                  </span>
                )}
              </div>
            </div>

            <div className="flex justify-between items-center py-1 border-b border-white/5">
              <span className="text-slate-400">自动挂载盘符</span>
              <span className="font-mono text-slate-200">本地 Z: 盘 (名为 MuYing)</span>
            </div>

            <div className="flex justify-between items-center py-1">
              <span className="text-slate-400">直达快捷方式</span>
              <span className="text-slate-200">桌面 [MuYing] 文件夹</span>
            </div>
          </div>

          {/* 下载大按钮 */}
          <a
            ref={downloadBtnRef}
            href={downloadUrl}
            className="flex items-center justify-center gap-2.5 w-full h-12 rounded-[8px] bg-gradient-to-r from-blue-600 to-indigo-600 text-white font-semibold text-sm shadow-lg shadow-blue-500/25 hover:opacity-95 active:scale-[0.99] transition-all"
          >
            <Download className="h-4 w-4" />
            <span>立即下载挂载脚本 ({filename})</span>
          </a>

          {/* 3 步保姆级教程 */}
          <div
            ref={tutorialRef}
            className="p-4 rounded-[12px] bg-slate-950/40 border border-white/10 space-y-3.5 text-left text-xs"
          >
            <div className="flex items-center gap-2 font-bold text-white text-sm">
              <Info className="h-4 w-4 text-blue-400" />
              <span>挂载脚本使用教程（极简 3 步）</span>
            </div>

            <div className="flex items-start gap-3">
              <div className="h-5 w-5 rounded-full bg-blue-600 text-white flex items-center justify-center shrink-0 font-bold text-[11px] mt-0.5">
                1
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-slate-200">点击上方按钮下载脚本</div>
                <div className="text-slate-400 leading-relaxed">
                  浏览器会自动保存 <code className="text-sky-300">{filename}</code> 到您的电脑（通常在“下载”文件夹）。
                </div>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="h-5 w-5 rounded-full bg-blue-600 text-white flex items-center justify-center shrink-0 font-bold text-[11px] mt-0.5">
                2
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-slate-200">在本地电脑双击运行该脚本</div>
                <div className="text-slate-400 leading-relaxed">
                  <span className="bg-amber-500/20 text-amber-300 border border-amber-500/40 px-1 py-0.5 rounded text-[10px] mr-1">
                    注意
                  </span>
                  若 Windows 弹出蓝色安全防护弹窗（“Windows 已保护你的电脑”），请点击{' '}
                  <strong className="text-white">【更多信息】</strong> → 点击{' '}
                  <strong className="text-white">【仍要运行】</strong> 即可。
                </div>
              </div>
            </div>

            <div className="flex items-start gap-3">
              <div className="h-5 w-5 rounded-full bg-blue-600 text-white flex items-center justify-center shrink-0 font-bold text-[11px] mt-0.5">
                3
              </div>
              <div className="space-y-0.5">
                <div className="font-medium text-slate-200">自动配置完成，直接使用</div>
                <div className="text-slate-400 leading-relaxed">
                  黑色窗口运行约 2 秒钟并提示成功：
                  <br />• 电脑将自动挂载出 <strong className="text-white">Z: 盘 (MuYing)</strong> 并自动打开文件夹；
                  <br />• <strong className="text-white">电脑桌面</strong> 会自动生成 <strong className="text-white">【MuYing】快捷方式</strong>，以后双击即可直达；
                  <br />• 凭据已自动加密保存，<strong className="text-emerald-400">以后每天开机永久免输入密码</strong>。
                </div>
              </div>
            </div>
          </div>

          <div className="text-center pt-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={onBack}
              className="text-slate-400 hover:text-white inline-flex items-center gap-1.5"
            >
              <ArrowLeft className="h-3.5 w-3.5" />
              <span>返回注册首页</span>
            </Button>
          </div>
        </div>
      </Card>
    </div>
  );
};
