import React, { useState, useEffect, useLayoutEffect, useRef } from 'react';
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
  const formPanelRef = useRef<HTMLDivElement>(null);

  // 1. 卡片首次挂载入场动画（严格遵循 prefers-reduced-motion，无旋转/无果冻回弹）
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
        '.gsap-stagger-item',
        { opacity: 0, y: 8 },
        { opacity: 1, y: 0, duration: 0.2, stagger: 0.04 },
        '-=0.15'
      );
    }, containerRef);

    return () => ctx.revert();
  }, []);

  // 2. 指示器位移：纯粹由 React 状态 tab 决定（Single Source of Truth）
  // 减少动态效果模式下也必须正确将指示器设置到目标位置；
  // overwrite: true 彻底解决快速连续切换导致的补间堆积/抖动问题
  useLayoutEffect(() => {
    if (!indicatorRef.current) return;

    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const targetX = tab === 'register' ? 0 : 100;

    if (prefersReducedMotion) {
      gsap.set(indicatorRef.current, { xPercent: targetX });
    } else {
      gsap.to(indicatorRef.current, {
        xPercent: targetX,
        duration: 0.22,
        ease: 'power2.out',
        overwrite: true,
      });
    }
  }, [tab]);

  // 3. 表单切换淡入：监听已挂载的 DOM 节点，避免动画操作已卸载 DOM，支持快速切换
  useEffect(() => {
    if (!formPanelRef.current) return;

    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (prefersReducedMotion) return;

    const ctx = gsap.context(() => {
      gsap.fromTo(
        formPanelRef.current,
        { opacity: 0.4, y: 4 },
        { opacity: 1, y: 0, duration: 0.18, ease: 'power2.out', overwrite: true }
      );
    }, formPanelRef);

    return () => ctx.revert();
  }, [tab]);

  const handleTabChange = (targetTab: 'register' | 'login') => {
    if (tab === targetTab) return;
    setError(null);
    setTab(targetTab);
  };

  const handleKeyDownTab = (e: React.KeyboardEvent, currentTab: 'register' | 'login') => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      handleTabChange(currentTab === 'register' ? 'login' : 'register');
    }
  };

  const handleKeyDownGroup = (e: React.KeyboardEvent, currentGroup: 'film' | 'writing') => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      setGroup(currentGroup === 'film' ? 'writing' : 'film');
    }
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
              <Film className="h-5 w-5" aria-hidden="true" />
            </div>
          </div>
          <CardTitle className="text-xl font-semibold text-foreground tracking-tight">
            MuYing 资产盘服务中心
          </CardTitle>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 精准滑块 Tab 栏：ARIA 语义 + 键盘左右键无障碍支持 */}
          <div
            role="tablist"
            aria-label="员工身份与操作切换"
            className="relative flex bg-sidebar p-1 rounded-[8px] border border-border overflow-hidden"
          >
            <div
              ref={indicatorRef}
              className="absolute top-1 bottom-1 left-1 w-[calc(50%-4px)] bg-secondary rounded-[6px] border border-border pointer-events-none"
              aria-hidden="true"
            />
            <button
              id="tab-register"
              role="tab"
              type="button"
              aria-selected={tab === 'register'}
              aria-controls="tabpanel-register"
              tabIndex={tab === 'register' ? 0 : -1}
              onClick={() => handleTabChange('register')}
              onKeyDown={(e) => handleKeyDownTab(e, 'register')}
              className={`relative z-10 flex-1 py-1.5 text-xs font-medium text-center transition-colors duration-150 select-none rounded-[6px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                tab === 'register' ? 'text-foreground' : 'text-muted hover:text-foreground'
              }`}
            >
              新员工注册
            </button>
            <button
              id="tab-login"
              role="tab"
              type="button"
              aria-selected={tab === 'login'}
              aria-controls="tabpanel-login"
              tabIndex={tab === 'login' ? 0 : -1}
              onClick={() => handleTabChange('login')}
              onKeyDown={(e) => handleKeyDownTab(e, 'login')}
              className={`relative z-10 flex-1 py-1.5 text-xs font-medium text-center transition-colors duration-150 select-none rounded-[6px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                tab === 'login' ? 'text-foreground' : 'text-muted hover:text-foreground'
              }`}
            >
              重新领脚本
            </button>
          </div>

          {error && (
            <div
              role="alert"
              aria-live="polite"
              className="p-3 bg-destructive-bg border border-destructive-border rounded-[8px] text-xs text-destructive-foreground flex items-start gap-2.5"
            >
              <AlertTriangle className="h-4 w-4 shrink-0 text-destructive mt-0.5" aria-hidden="true" />
              <div className="leading-relaxed">{error}</div>
            </div>
          )}

          {/* 表单容器：安全挂载 ref，彻底规避已卸载 DOM 动画 */}
          <div ref={formPanelRef}>
            {tab === 'register' ? (
              <form
                id="tabpanel-register"
                role="tabpanel"
                aria-labelledby="tab-register"
                onSubmit={handleRegister}
                className="space-y-3.5"
              >
                <div className="gsap-stagger-item space-y-1.5">
                  <label htmlFor="reg-fullname" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <User className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>姓名</span>
                  </label>
                  <Input
                    id="reg-fullname"
                    required
                    placeholder="请输入全名以方便对接（如：张三）"
                    value={fullname}
                    onChange={(e) => setFullname(e.target.value)}
                  />
                </div>

                <div className="gsap-stagger-item space-y-1.5">
                  <label htmlFor="reg-password" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <KeyRound className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>设置访问密码</span>
                  </label>
                  <Input
                    id="reg-password"
                    type="password"
                    required
                    placeholder="设置 6 位以上密码并牢记"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                  />
                </div>

                {/* 业务组单选组：ARIA radiogroup 与焦点环 */}
                <div className="gsap-stagger-item space-y-1.5">
                  <span id="group-label" className="text-xs font-medium text-foreground">所属业务组</span>
                  <div
                    role="radiogroup"
                    aria-labelledby="group-label"
                    className="grid grid-cols-2 gap-2.5"
                  >
                    <button
                      type="button"
                      role="radio"
                      aria-checked={group === 'film'}
                      tabIndex={group === 'film' ? 0 : -1}
                      onClick={() => setGroup('film')}
                      onKeyDown={(e) => handleKeyDownGroup(e, 'film')}
                      className={`p-3 rounded-[8px] border text-left transition-colors duration-150 relative focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                        group === 'film'
                          ? 'border-accent bg-secondary text-foreground'
                          : 'border-border bg-sidebar hover:border-border-hover text-muted'
                      }`}
                    >
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-2">
                          <Video className={`h-4 w-4 ${group === 'film' ? 'text-accent' : 'text-muted'}`} aria-hidden="true" />
                          <span className="font-semibold text-xs text-foreground">影视组</span>
                        </div>
                        {group === 'film' && (
                          <Check className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
                        )}
                      </div>
                    </button>

                    <button
                      type="button"
                      role="radio"
                      aria-checked={group === 'writing'}
                      tabIndex={group === 'writing' ? 0 : -1}
                      onClick={() => setGroup('writing')}
                      onKeyDown={(e) => handleKeyDownGroup(e, 'writing')}
                      className={`p-3 rounded-[8px] border text-left transition-colors duration-150 relative focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                        group === 'writing'
                          ? 'border-accent bg-secondary text-foreground'
                          : 'border-border bg-sidebar hover:border-border-hover text-muted'
                      }`}
                    >
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-2">
                          <PenLine className={`h-4 w-4 ${group === 'writing' ? 'text-accent' : 'text-muted'}`} aria-hidden="true" />
                          <span className="font-semibold text-xs text-foreground">写作组</span>
                        </div>
                        {group === 'writing' && (
                          <Check className="h-3.5 w-3.5 text-accent" aria-hidden="true" />
                        )}
                      </div>
                    </button>
                  </div>
                </div>

                {/* 提示栏：低饱和琥珀色，重要提醒 */}
                <div className="gsap-stagger-item p-2.5 bg-warning-bg border border-warning-border rounded-[8px] text-xs text-warning flex items-center gap-2">
                  <Info className="h-4 w-4 shrink-0 text-warning" aria-hidden="true" />
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
              <form
                id="tabpanel-login"
                role="tabpanel"
                aria-labelledby="tab-login"
                onSubmit={handleVerify}
                className="space-y-3.5"
              >
                <div className="space-y-1.5">
                  <label htmlFor="login-fullname" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <User className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>姓名</span>
                  </label>
                  <Input
                    id="login-fullname"
                    required
                    placeholder="请输入注册时的全名"
                    value={loginName}
                    onChange={(e) => setLoginName(e.target.value)}
                  />
                </div>

                <div className="space-y-1.5">
                  <label htmlFor="login-password" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <KeyRound className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>访问密码</span>
                  </label>
                  <Input
                    id="login-password"
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
        </div>
      </Card>
    </div>
  );
};
