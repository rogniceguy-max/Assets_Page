import React, { useState, useEffect, useRef } from 'react';
import gsap from 'gsap';
import { 
  Film, 
  Video, 
  PenLine, 
  AlertTriangle, 
  Check, 
  KeyRound,
  User,
  Info
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
  const indicatorRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLFormElement>(null);

  // GSAP 规范：微位移、平滑入场（150-300ms，严格支持 prefers-reduced-motion，无果冻旋转回弹）
  useEffect(() => {
    if (!containerRef.current) return;

    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (prefersReducedMotion) return;

    const ctx = gsap.context(() => {
      const tl = gsap.timeline({ defaults: { ease: 'power2.out' } });

      tl.fromTo(
        containerRef.current,
        { opacity: 0, y: 12 },
        { opacity: 1, y: 0, duration: 0.3 }
      );

      tl.fromTo(
        '.gsap-stagger-item',
        { opacity: 0, y: 8 },
        { opacity: 1, y: 0, duration: 0.22, stagger: 0.04 },
        '-=0.15'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  // GSAP 规范：平滑位移 Tab 滑块（200ms，无弹性 overshoot）
  const handleTabChange = (targetTab: 'register' | 'login') => {
    if (tab === targetTab) return;
    setError(null);

    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const isReg = targetTab === 'register';

    if (indicatorRef.current && !prefersReducedMotion) {
      gsap.to(indicatorRef.current, {
        xPercent: isReg ? 0 : 100,
        duration: 0.22,
        ease: 'power2.out',
      });
    }

    if (formRef.current && !prefersReducedMotion) {
      gsap.fromTo(
        formRef.current,
        { opacity: 0.5, y: 4 },
        { opacity: 1, y: 0, duration: 0.2, ease: 'power2.out' }
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
    <div ref={containerRef} className="w-full max-w-[480px]">
      <Card className="border border-border bg-card shadow-panel">
        <CardHeader className="text-center pb-4 pt-7">
          <div className="flex justify-center mb-2.5">
            <div className="h-10 w-10 rounded-[8px] bg-secondary border border-border flex items-center justify-center text-accent">
              <Film className="h-5 w-5" />
            </div>
          </div>
          <CardTitle className="text-xl font-semibold text-foreground tracking-tight">
            MuYing 资产盘服务中心
          </CardTitle>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 精准滑块 Tab 栏：中性灰阶基座 + 香槟铜金精细边框/背景 */}
          <div className="relative flex bg-sidebar p-1 rounded-[8px] border border-border overflow-hidden">
            <div
              ref={indicatorRef}
              className="absolute top-1 bottom-1 left-1 w-[calc(50%-4px)] bg-secondary rounded-[6px] border border-border pointer-events-none"
            />
            <button
              type="button"
              onClick={() => handleTabChange('register')}
              className={`relative z-10 flex-1 py-1.5 text-xs font-medium text-center transition-colors duration-150 select-none ${
                tab === 'register' ? 'text-foreground' : 'text-muted hover:text-foreground'
              }`}
            >
              新员工注册
            </button>
            <button
              type="button"
              onClick={() => handleTabChange('login')}
              className={`relative z-10 flex-1 py-1.5 text-xs font-medium text-center transition-colors duration-150 select-none ${
                tab === 'login' ? 'text-foreground' : 'text-muted hover:text-foreground'
              }`}
            >
              重新领脚本
            </button>
          </div>

          {error && (
            <div className="p-3 bg-destructive-bg border border-destructive-border rounded-[8px] text-xs text-destructive-foreground flex items-start gap-2.5">
              <AlertTriangle className="h-4 w-4 shrink-0 text-destructive mt-0.5" />
              <div className="leading-relaxed">{error}</div>
            </div>
          )}

          {tab === 'register' ? (
            <form ref={formRef} onSubmit={handleRegister} className="space-y-3.5">
              <div className="gsap-stagger-item space-y-1.5">
                <label className="text-xs font-medium text-foreground flex items-center gap-1.5">
                  <User className="h-3.5 w-3.5 text-muted" />
                  <span>姓名</span>
                </label>
                <Input
                  required
                  placeholder="请输入全名以方便对接（如：张三）"
                  value={fullname}
                  onChange={(e) => setFullname(e.target.value)}
                />
              </div>

              <div className="gsap-stagger-item space-y-1.5">
                <label className="text-xs font-medium text-foreground flex items-center gap-1.5">
                  <KeyRound className="h-3.5 w-3.5 text-muted" />
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

              {/* 业务组选择：统一工业语言，通过选中边框、背景微明度与图标区分，拒绝紫绿发光 */}
              <div className="gsap-stagger-item space-y-1.5">
                <label className="text-xs font-medium text-foreground">所属业务组</label>
                <div className="grid grid-cols-2 gap-2.5">
                  <button
                    type="button"
                    onClick={() => setGroup('film')}
                    className={`p-3 rounded-[8px] border text-left transition-colors duration-150 relative ${
                      group === 'film'
                        ? 'border-accent bg-secondary text-foreground'
                        : 'border-border bg-sidebar hover:border-border-hover text-muted'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <Video className={`h-4 w-4 ${group === 'film' ? 'text-accent' : 'text-muted'}`} />
                        <span className="font-semibold text-xs text-foreground">影视组</span>
                      </div>
                      {group === 'film' && (
                        <Check className="h-3.5 w-3.5 text-accent" />
                      )}
                    </div>
                  </button>

                  <button
                    type="button"
                    onClick={() => setGroup('writing')}
                    className={`p-3 rounded-[8px] border text-left transition-colors duration-150 relative ${
                      group === 'writing'
                        ? 'border-accent bg-secondary text-foreground'
                        : 'border-border bg-sidebar hover:border-border-hover text-muted'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-2">
                        <PenLine className={`h-4 w-4 ${group === 'writing' ? 'text-accent' : 'text-muted'}`} />
                        <span className="font-semibold text-xs text-foreground">写作组</span>
                      </div>
                      {group === 'writing' && (
                        <Check className="h-3.5 w-3.5 text-accent" />
                      )}
                    </div>
                  </button>
                </div>
              </div>

              {/* 提示栏：低饱和琥珀色，重要提醒 */}
              <div className="gsap-stagger-item p-2.5 bg-warning-bg border border-warning-border rounded-[8px] text-xs text-warning flex items-center gap-2">
                <Info className="h-4 w-4 shrink-0 text-warning" />
                <span className="font-medium">重要提醒：只能注册一次</span>
              </div>

              <Button
                type="submit"
                disabled={loading}
                className="gsap-stagger-item w-full h-10 text-xs font-semibold mt-1"
              >
                {loading ? '正在开通专属权限中...' : '立即注册'}
              </Button>
            </form>
          ) : (
            <form ref={formRef} onSubmit={handleVerify} className="space-y-3.5">
              <div className="space-y-1.5">
                <label className="text-xs font-medium text-foreground flex items-center gap-1.5">
                  <User className="h-3.5 w-3.5 text-muted" />
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
                <label className="text-xs font-medium text-foreground flex items-center gap-1.5">
                  <KeyRound className="h-3.5 w-3.5 text-muted" />
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
                className="w-full h-10 text-xs font-semibold mt-2"
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
