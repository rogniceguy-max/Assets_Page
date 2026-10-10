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
  Info,
  RefreshCw,
  LogIn
} from 'lucide-react';
import { Card, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';

export type AuthTab = 'login' | 'register' | 'remount';

export interface AuthSuccessData {
  fullname: string;
  token: string;
  linux_username?: string;
  group_type?: string;
  mode: AuthTab;
}

interface RegisterCardProps {
  onSuccess: (data: AuthSuccessData) => void;
  defaultTab?: AuthTab;
}

export const RegisterCard: React.FC<RegisterCardProps> = ({ 
  onSuccess,
  defaultTab = 'login'
}) => {
  const [tab, setTab] = useState<AuthTab>(defaultTab);
  const [group, setGroup] = useState<'film' | 'writing'>('film');
  
  // 注册表单状态
  const [regFullname, setRegFullname] = useState('');
  const [regPassword, setRegPassword] = useState('');

  // 登录表单状态
  const [loginUser, setLoginUser] = useState('');
  const [loginPass, setLoginPass] = useState('');

  // 重新挂载表单状态
  const [remountName, setRemountName] = useState('');
  const [remountPass, setRemountPass] = useState('');

  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const containerRef = useRef<HTMLDivElement>(null);
  const indicatorRef = useRef<HTMLDivElement>(null);
  const formPanelRef = useRef<HTMLDivElement>(null);

  // 1. 卡片首次挂载入场动画（遵循 prefers-reduced-motion，无大幅度旋转/果冻回弹）
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

  // 2. 精准三段式指示器位移（由 React 状态 tab 驱动，overwrite: true 杜绝高频冲突）
  useLayoutEffect(() => {
    if (!indicatorRef.current) return;

    const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const targetX = tab === 'login' ? 0 : tab === 'register' ? 100 : 200;

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

  // 3. 表单切换淡入：监听已挂载的 DOM 节点，避免动画操作已卸载 DOM
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

  const tabs: AuthTab[] = ['login', 'register', 'remount'];

  const handleTabChange = (targetTab: AuthTab) => {
    if (tab === targetTab) return;
    setError(null);
    setTab(targetTab);
  };

  const handleKeyDownTab = (e: React.KeyboardEvent, currentTab: AuthTab) => {
    const currentIndex = tabs.indexOf(currentTab);
    if (e.key === 'ArrowRight') {
      e.preventDefault();
      const nextIndex = (currentIndex + 1) % tabs.length;
      handleTabChange(tabs[nextIndex]);
    } else if (e.key === 'ArrowLeft') {
      e.preventDefault();
      const prevIndex = (currentIndex - 1 + tabs.length) % tabs.length;
      handleTabChange(tabs[prevIndex]);
    }
  };

  const handleKeyDownGroup = (e: React.KeyboardEvent, currentGroup: 'film' | 'writing') => {
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      setGroup(currentGroup === 'film' ? 'writing' : 'film');
    }
  };

  // 登录处理
  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!loginUser.trim() || !loginPass) {
      setError('请填写姓名/账号与访问密码');
      return;
    }

    setLoading(true);
    try {
      const resp = await fetch('/api/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          fullname: loginUser.trim(),
          password: loginPass,
        }),
      });
      const data = await resp.json();
      if (resp.ok && data.success) {
        onSuccess({
          fullname: data.fullname || loginUser.trim(),
          token: data.token,
          linux_username: data.linux_username,
          group_type: data.group_type,
          mode: 'login'
        });
      } else {
        setError(data.error || '姓名或密码错误');
      }
    } catch (err: any) {
      setError('连接登录接口失败: ' + err.message);
    } finally {
      setLoading(false);
    }
  };

  // 注册处理
  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!regFullname.trim() || !regPassword) {
      setError('请填写姓名与密码');
      return;
    }

    setLoading(true);
    try {
      const resp = await fetch('/api/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          fullname: regFullname.trim(),
          password: regPassword,
          group_type: group,
        }),
      });
      const data = await resp.json();
      if (resp.ok && data.success) {
        onSuccess({
          fullname: regFullname.trim(),
          token: data.token,
          linux_username: data.linux_username,
          group_type: data.group_type,
          mode: 'register'
        });
      } else {
        setError(data.error || '注册失败，请稍后重试');
      }
    } catch (err: any) {
      setError('连接服务器失败: ' + err.message);
    } finally {
      setLoading(false);
    }
  };

  // 重新挂载处理
  const handleRemount = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);

    if (!remountName.trim() || !remountPass) {
      setError('请填写姓名与访问密码');
      return;
    }

    setLoading(true);
    try {
      const resp = await fetch('/api/verify', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          fullname: remountName.trim(),
          password: remountPass,
        }),
      });
      const data = await resp.json();
      if (resp.ok && data.success) {
        onSuccess({
          fullname: data.fullname || remountName.trim(),
          token: data.token,
          linux_username: data.linux_username,
          group_type: data.group_type,
          mode: 'remount'
        });
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
            沐影
          </CardTitle>
        </CardHeader>

        <div className="px-6 pb-7 space-y-4">
          {/* 精准三栏滑块 Tab 栏：登录 | 注册 | 重新挂载 */}
          <div
            role="tablist"
            aria-label="员工身份与操作切换"
            className="relative flex bg-sidebar p-1 rounded-[8px] border border-border overflow-hidden"
          >
            {/* 三段式滑块指示器：严格计算 w=[calc((100%-8px)/3)] */}
            <div
              ref={indicatorRef}
              className="absolute top-1 bottom-1 left-1 w-[calc((100%-8px)/3)] bg-secondary rounded-[6px] border border-border pointer-events-none"
              aria-hidden="true"
            />
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
                tab === 'login' ? 'text-foreground font-semibold' : 'text-muted hover:text-foreground'
              }`}
            >
              登录
            </button>
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
                tab === 'register' ? 'text-foreground font-semibold' : 'text-muted hover:text-foreground'
              }`}
            >
              注册
            </button>
            <button
              id="tab-remount"
              role="tab"
              type="button"
              aria-selected={tab === 'remount'}
              aria-controls="tabpanel-remount"
              tabIndex={tab === 'remount' ? 0 : -1}
              onClick={() => handleTabChange('remount')}
              onKeyDown={(e) => handleKeyDownTab(e, 'remount')}
              className={`relative z-10 flex-1 py-1.5 text-xs font-medium text-center transition-colors duration-150 select-none rounded-[6px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent ${
                tab === 'remount' ? 'text-foreground font-semibold' : 'text-muted hover:text-foreground'
              }`}
            >
              重新挂载
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

          {/* 表单容器：GSAP 上浮淡入 */}
          <div ref={formPanelRef}>
            {/* 1. 登录面板 */}
            {tab === 'login' && (
              <form
                id="tabpanel-login"
                role="tabpanel"
                aria-labelledby="tab-login"
                onSubmit={handleLogin}
                className="space-y-3.5"
              >
                <div className="space-y-1.5">
                  <label htmlFor="login-username" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <User className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>姓名 / 账号</span>
                  </label>
                  <Input
                    id="login-username"
                    required
                    placeholder="请输入您的姓名或系统账号"
                    value={loginUser}
                    onChange={(e) => setLoginUser(e.target.value)}
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
                    placeholder="请输入您的访问密码"
                    value={loginPass}
                    onChange={(e) => setLoginPass(e.target.value)}
                  />
                </div>

                <Button
                  type="submit"
                  disabled={loading}
                  className="w-full h-10 text-xs font-semibold mt-2 inline-flex items-center justify-center gap-1.5"
                >
                  <LogIn className="h-4 w-4" aria-hidden="true" />
                  <span>{loading ? '正在验证登录...' : '立即登录'}</span>
                </Button>
              </form>
            )}

            {/* 2. 注册面板 */}
            {tab === 'register' && (
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
                    value={regFullname}
                    onChange={(e) => setRegFullname(e.target.value)}
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
                    value={regPassword}
                    onChange={(e) => setRegPassword(e.target.value)}
                  />
                </div>

                {/* 业务组单选组 */}
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
            )}

            {/* 3. 重新挂载面板 */}
            {tab === 'remount' && (
              <form
                id="tabpanel-remount"
                role="tabpanel"
                aria-labelledby="tab-remount"
                onSubmit={handleRemount}
                className="space-y-3.5"
              >
                <div className="p-2.5 bg-secondary border border-border rounded-[8px] text-xs text-muted flex items-center gap-2">
                  <RefreshCw className="h-4 w-4 shrink-0 text-accent" aria-hidden="true" />
                  <span>更换电脑或盘符掉线？输入凭据生成 60 秒新挂载脚本</span>
                </div>

                <div className="space-y-1.5">
                  <label htmlFor="remount-name" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <User className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>姓名 / 账号</span>
                  </label>
                  <Input
                    id="remount-name"
                    required
                    placeholder="请输入注册时的姓名或系统账号"
                    value={remountName}
                    onChange={(e) => setRemountName(e.target.value)}
                  />
                </div>

                <div className="space-y-1.5">
                  <label htmlFor="remount-password" className="text-xs font-medium text-foreground flex items-center gap-1.5">
                    <KeyRound className="h-3.5 w-3.5 text-muted" aria-hidden="true" />
                    <span>访问密码</span>
                  </label>
                  <Input
                    id="remount-password"
                    type="password"
                    required
                    placeholder="请输入您的访问密码"
                    value={remountPass}
                    onChange={(e) => setRemountPass(e.target.value)}
                  />
                </div>

                <Button
                  type="submit"
                  disabled={loading}
                  className="w-full h-10 text-xs font-semibold mt-2 inline-flex items-center justify-center gap-1.5"
                >
                  <RefreshCw className="h-4 w-4" aria-hidden="true" />
                  <span>{loading ? '正在验证并生成凭据...' : '验证并重新挂载'}</span>
                </Button>
              </form>
            )}
          </div>
        </div>
      </Card>
    </div>
  );
};
