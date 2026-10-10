import React, { useState, useEffect, useRef } from 'react';
import gsap from 'gsap';
import { 
  Film, 
  Video, 
  PenLine, 
  AlertTriangle, 
  CheckCircle2, 
  KeyRound,
  User,
  Sparkles
} from 'lucide-react';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';

interface RegisterCardProps {
  onSuccess: (fullname: string) => void;
}

export const RegisterCard: React.FC<RegisterCardProps> = ({ onSuccess }) => {
  const [tab, setTab] = useState<'register' | 'login'>('register');
  const [group, setGroup] = useState<'film' | 'writing'>('film');
  const [fullname, setFullname] = useState('');
  const [password, setPassword] = useState('');
  const [loginName, setLoginName] = useState('');
  const [loginPass, setLoginPass] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const containerRef = useRef<HTMLDivElement>(null);
  const logoRef = useRef<HTMLDivElement>(null);
  const titleRef = useRef<HTMLHeadingElement>(null);
  const tabBoxRef = useRef<HTMLDivElement>(null);
  const indicatorRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLFormElement>(null);

  // 🌟 GSAP 方案 3：多级错峰电影感入场动画
  useEffect(() => {
    if (!containerRef.current) return;

    const ctx = gsap.context(() => {
      const tl = gsap.timeline();

      tl.fromTo(
        containerRef.current,
        { opacity: 0, scale: 0.96, y: 24 },
        { opacity: 1, scale: 1, y: 0, duration: 0.55, ease: 'power3.out' }
      );

      tl.fromTo(
        logoRef.current,
        { scale: 0, rotation: -20 },
        { scale: 1, rotation: 0, duration: 0.5, ease: 'back.out(2)' },
        '-=0.3'
      );

      tl.fromTo(
        titleRef.current,
        { opacity: 0, y: 12 },
        { opacity: 1, y: 0, duration: 0.35, ease: 'power2.out' },
        '-=0.25'
      );

      tl.fromTo(
        tabBoxRef.current,
        { opacity: 0, y: 12 },
        { opacity: 1, y: 0, duration: 0.35, ease: 'power2.out' },
        '-=0.2'
      );

      tl.fromTo(
        '.stagger-item',
        { opacity: 0, y: 16 },
        { opacity: 1, y: 0, duration: 0.4, stagger: 0.07, ease: 'power2.out' },
        '-=0.15'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  // 🌟 GSAP 方案 2：弹性滑块标签切换
  const handleTabChange = (targetTab: 'register' | 'login') => {
    if (tab === targetTab) return;
    setError(null);

    const isReg = targetTab === 'register';
    if (indicatorRef.current) {
      gsap.to(indicatorRef.current, {
        xPercent: isReg ? 0 : 100,
        duration: 0.4,
        ease: 'back.out(1.25)',
      });
    }

    if (formRef.current) {
      gsap.fromTo(
        formRef.current,
        { opacity: 0.3, y: 8 },
        { opacity: 1, y: 0, duration: 0.3, ease: 'power2.out' }
      );
    }

    setTab(targetTab);
  };

  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!fullname.trim() || !password) {
      setError('请填写姓名与密码');
      return;
    }

    setLoading(true);
    try {
      const resp = await fetch('/api/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          fullname: fullname.trim(),
          password,
          group_type: group,
        }),
      });
      const data = await resp.json();
      if (resp.ok && data.success) {
        onSuccess(fullname.trim());
      } else {
        setError(data.error || '注册失败，请稍后重试');
      }
    } catch (err: any) {
      setError('连接服务器失败: ' + err.message);
    } finally {
      setLoading(false);
    }
  };

  const handleVerify = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!loginName.trim() || !loginPass) {
      setError('请填写姓名与密码');
      return;
    }

    setLoading(true);
    try {
      const resp = await fetch('/api/verify', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          fullname: loginName.trim(),
          password: loginPass,
        }),
      });
      const data = await resp.json();
      if (resp.ok && data.success) {
        onSuccess(loginName.trim());
      } else {
        setError(data.error || '姓名或密码错误');
      }
    } catch (err: any) {
      setError('连接验证失败: ' + err.message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <div ref={containerRef} className="w-full max-w-[500px]">
      <Card className="border border-white/10 shadow-2xl bg-slate-900/80 backdrop-blur-xl">
        <CardHeader className="text-center pb-5 pt-8">
          <div className="flex justify-center mb-3">
            <div
              ref={logoRef}
              className="h-14 w-14 rounded-2xl bg-gradient-to-tr from-blue-600 to-indigo-500 flex items-center justify-center text-white shadow-lg shadow-blue-500/25"
            >
              <Film className="h-7 w-7" />
            </div>
          </div>
          <CardTitle ref={titleRef} className="text-2xl font-bold text-white tracking-tight">
            MuYing 资产盘服务中心
          </CardTitle>
        </CardHeader>

        <div className="px-7 pb-8 space-y-5">
          {/* 弹性滑块 Tab 栏 */}
          <div
            ref={tabBoxRef}
            className="relative flex bg-slate-950/70 p-1 rounded-xl border border-white/10 overflow-hidden"
          >
            <div
              ref={indicatorRef}
              className="absolute top-1 bottom-1 left-1 w-[calc(50%-4px)] bg-gradient-to-r from-blue-600 to-primary rounded-[9px] shadow-md shadow-blue-500/30 pointer-events-none"
            />
            <button
              type="button"
              onClick={() => handleTabChange('register')}
              className={`relative z-10 flex-1 py-2 text-sm font-semibold transition-colors duration-200 text-center ${
                tab === 'register' ? 'text-white' : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              新员工注册
            </button>
            <button
              type="button"
              onClick={() => handleTabChange('login')}
              className={`relative z-10 flex-1 py-2 text-sm font-semibold transition-colors duration-200 text-center ${
                tab === 'login' ? 'text-white' : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              重新领脚本
            </button>
          </div>

          {error && (
            <div className="p-3.5 bg-red-500/10 border border-red-500/30 rounded-[8px] text-xs text-red-300 flex items-start gap-2.5">
              <AlertTriangle className="h-4 w-4 shrink-0 text-red-400 mt-0.5" />
              <div className="leading-relaxed">{error}</div>
            </div>
          )}

          {tab === 'register' ? (
            <form ref={formRef} onSubmit={handleRegister} className="space-y-4">
              <div className="stagger-item space-y-1.5">
                <label className="text-sm font-medium text-slate-200 flex items-center gap-1.5">
                  <User className="h-3.5 w-3.5 text-slate-400" />
                  <span>姓名</span>
                </label>
                <Input
                  required
                  placeholder="请输入全名以方便对接（如：张三）"
                  value={fullname}
                  onChange={(e) => setFullname(e.target.value)}
                />
              </div>

              <div className="stagger-item space-y-1.5">
                <label className="text-sm font-medium text-slate-200 flex items-center gap-1.5">
                  <KeyRound className="h-3.5 w-3.5 text-slate-400" />
                  <span>设置访问密码</span>
                </label>
                <Input
                  type="password"
                  required
                  placeholder="设置 6 位以上密码并牢记"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
              </div>

              <div className="stagger-item space-y-2">
                <label className="text-sm font-medium text-slate-200">所属业务组</label>
                <div className="grid grid-cols-2 gap-3">
                  <button
                    type="button"
                    onClick={() => setGroup('film')}
                    className={`p-3.5 rounded-[12px] border text-left transition-all relative ${
                      group === 'film'
                        ? 'border-purple-500/70 bg-purple-500/15 shadow-sm shadow-purple-500/20'
                        : 'border-white/10 bg-slate-950/40 hover:border-slate-700'
                    }`}
                  >
                    <div className="flex items-center gap-2 mb-1.5">
                      <div className="p-1 rounded-md bg-purple-500/20 text-purple-400">
                        <Video className="h-4 w-4" />
                      </div>
                      <span className="font-bold text-sm text-white">影视组</span>
                    </div>
                    <p className="text-[11px] text-slate-400 leading-normal">
                      拥有【01_影视资源库】完全读写权限，文学创作库只读
                    </p>
                  </button>

                  <button
                    type="button"
                    onClick={() => setGroup('writing')}
                    className={`p-3.5 rounded-[12px] border text-left transition-all relative ${
                      group === 'writing'
                        ? 'border-emerald-500/70 bg-emerald-500/15 shadow-sm shadow-emerald-500/20'
                        : 'border-white/10 bg-slate-950/40 hover:border-slate-700'
                    }`}
                  >
                    <div className="flex items-center gap-2 mb-1.5">
                      <div className="p-1 rounded-md bg-emerald-500/20 text-emerald-400">
                        <PenLine className="h-4 w-4" />
                      </div>
                      <span className="font-bold text-sm text-white">写作组</span>
                    </div>
                    <p className="text-[11px] text-slate-400 leading-normal">
                      拥有【02_文学创作库】完全读写权限，影视资源库只读
                    </p>
                  </button>
                </div>
              </div>

              <div className="stagger-item p-3 bg-amber-500/10 border border-amber-500/30 rounded-[8px] text-xs text-amber-300 leading-relaxed flex items-start gap-2">
                <Sparkles className="h-4 w-4 shrink-0 text-amber-400 mt-0.5" />
                <div>
                  <strong>重要提醒：只能注册一次</strong>（系统已绑定本机 IP），提交后将自动为您开通权限并生成专属挂载脚本。
                </div>
              </div>

              <Button
                type="submit"
                disabled={loading}
                className="stagger-item w-full h-11 text-sm font-semibold"
              >
                {loading ? '正在开通专属权限中...' : '立即注册'}
              </Button>
            </form>
          ) : (
            <form ref={formRef} onSubmit={handleVerify} className="space-y-4">
              <div className="space-y-1.5">
                <label className="text-sm font-medium text-slate-200 flex items-center gap-1.5">
                  <User className="h-3.5 w-3.5 text-slate-400" />
                  <span>姓名</span>
                </label>
                <Input
                  required
                  placeholder="请输入注册时的全名"
                  value={loginName}
                  onChange={(e) => setLoginName(e.target.value)}
                />
              </div>

              <div className="space-y-1.5">
                <label className="text-sm font-medium text-slate-200 flex items-center gap-1.5">
                  <KeyRound className="h-3.5 w-3.5 text-slate-400" />
                  <span>访问密码</span>
                </label>
                <Input
                  type="password"
                  required
                  placeholder="请输入您的密码"
                  value={loginPass}
                  onChange={(e) => setLoginPass(e.target.value)}
                />
              </div>

              <Button
                type="submit"
                disabled={loading}
                className="w-full h-11 text-sm font-semibold mt-2"
              >
                {loading ? '正在验证中...' : '验证并重新获取专属脚本'}
              </Button>
            </form>
          )}
        </div>
      </Card>
    </div>
  );
};
