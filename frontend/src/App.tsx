import React, { useEffect, useState } from 'react';
import { LoaderCircle } from 'lucide-react';
import { Header } from '@/components/layout/Header';
import { RegisterCard } from '@/components/registration/RegisterCard';
import { FileWorkspace } from '@/components/files/FileWorkspace';

type AuthState = 'checking' | 'anonymous' | 'authenticated';

interface SessionPayload {
  success?: boolean;
  authenticated?: boolean;
  user?: { username?: string };
}

export const App: React.FC = () => {
  const [authState, setAuthState] = useState<AuthState>('checking');
  const [startupError, setStartupError] = useState<string | null>(null);

  useEffect(() => {
    let current = true;

    const checkSession = async () => {
      try {
        const response = await fetch('/api/session', {
          credentials: 'same-origin',
          cache: 'no-store',
        });
        const data = (await response.json().catch(() => ({}))) as SessionPayload;
        const hasSession =
          response.ok &&
          (data.success === true || data.authenticated === true) &&
          typeof data.user?.username === 'string';

        if (!current) return;
        setAuthState(hasSession ? 'authenticated' : 'anonymous');
        if (!response.ok && response.status >= 500) {
          setStartupError('本地服务暂时不可用，请确认服务已启动后重试。');
        }
      } catch {
        if (!current) return;
        setAuthState('anonymous');
        setStartupError('无法连接本地服务，请确认服务已启动后重试。');
      }
    };

    void checkSession();
    return () => {
      current = false;
    };
  }, []);

  if (authState === 'authenticated') {
    return (
      <div className="h-screen overflow-hidden bg-background p-3 text-slate-100 md:p-5">
        <FileWorkspace
          onLogout={() => {
            setAuthState('anonymous');
            setStartupError(null);
          }}
        />
      </div>
    );
  }

  return (
    <div className="flex h-screen flex-col overflow-hidden bg-background text-slate-100">
      <Header showSidebarToggle={false} />
      <main className="relative flex flex-1 items-center justify-center overflow-y-auto p-4 md:p-8">
        <div className="pointer-events-none absolute left-1/3 top-1/4 h-96 w-96 -translate-x-1/2 -translate-y-1/2 rounded-full bg-blue-600/10 blur-3xl" />
        <div className="pointer-events-none absolute bottom-1/4 right-1/4 h-96 w-96 rounded-full bg-purple-600/10 blur-3xl" />

        {authState === 'checking' ? (
          <div className="flex items-center gap-3 rounded-xl border border-white/10 bg-slate-900/80 px-5 py-4 text-sm text-slate-300">
            <LoaderCircle className="h-4 w-4 animate-spin text-sky-300" aria-hidden="true" />
            正在检查本地登录状态…
          </div>
        ) : (
          <div className="relative w-full max-w-[500px]">
            {startupError && (
              <div className="mb-4 rounded-lg border border-amber-400/20 bg-amber-400/10 px-4 py-3 text-sm text-amber-100">
                {startupError}
              </div>
            )}
            <RegisterCard
              onSuccess={() => {
                setStartupError(null);
                setAuthState('authenticated');
              }}
            />
          </div>
        )}
      </main>
    </div>
  );
};

export default App;
