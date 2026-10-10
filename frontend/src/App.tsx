import React, { useState } from 'react';
import { Header } from '@/components/layout/Header';
import { Sidebar } from '@/components/layout/Sidebar';
import { RegisterCard } from '@/components/registration/RegisterCard';
import { SuccessCard } from '@/components/registration/SuccessCard';
import { Video, PenTool, FolderTree, Users, Sparkles } from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';

export const App: React.FC = () => {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [activeNav, setActiveNav] = useState('onboarding');
  const [registeredUser, setRegisteredUser] = useState<string | null>(null);

  return (
    <div className="flex flex-col h-screen overflow-hidden bg-background text-slate-100 font-sans select-none">
      {/* 顶部工具栏 (56–64px) */}
      <Header sidebarOpen={sidebarOpen} setSidebarOpen={setSidebarOpen} />

      <div className="flex flex-1 overflow-hidden relative">
        {/* 左侧可折叠导航栏 (展开 240px，收起 64px) */}
        <Sidebar
          open={sidebarOpen}
          setOpen={setSidebarOpen}
          activeNav={activeNav}
          setActiveNav={setActiveNav}
        />

        {/* 主内容区域 */}
        <main className="flex-1 overflow-y-auto p-4 md:p-8 flex items-center justify-center relative">
          {/* 背景微光氛围层 */}
          <div className="absolute top-1/4 left-1/3 -translate-x-1/2 -translate-y-1/2 w-96 h-96 bg-blue-600/10 rounded-full blur-3xl pointer-events-none" />
          <div className="absolute bottom-1/4 right-1/4 w-96 h-96 bg-purple-600/10 rounded-full blur-3xl pointer-events-none" />

          {activeNav === 'onboarding' ? (
            registeredUser ? (
              <SuccessCard
                fullname={registeredUser}
                onBack={() => setRegisteredUser(null)}
              />
            ) : (
              <RegisterCard onSuccess={(name) => setRegisteredUser(name)} />
            )
          ) : (
            /* 预留拓展模块展示位 */
            <div className="w-full max-w-lg text-center">
              <Card className="p-8 border border-white/10 bg-slate-900/80 backdrop-blur-xl">
                <CardHeader>
                  <div className="flex justify-center mb-3">
                    <div className="h-12 w-12 rounded-xl bg-blue-500/10 border border-blue-500/30 flex items-center justify-center text-blue-400">
                      {activeNav === 'film' && <Video className="h-6 w-6" />}
                      {activeNav === 'writing' && <PenTool className="h-6 w-6" />}
                      {activeNav === 'assets' && <FolderTree className="h-6 w-6" />}
                      {activeNav === 'members' && <Users className="h-6 w-6" />}
                    </div>
                  </div>
                  <CardTitle className="text-xl font-bold text-white">
                    {activeNav === 'film' && '01_影视资源库 (在线素材流)'}
                    {activeNav === 'writing' && '02_文学创作库 (剧本大纲协作)'}
                    {activeNav === 'assets' && '00_公共素材资产总库'}
                    {activeNav === 'members' && '团队成员与目录权限管理中心'}
                  </CardTitle>
                  <CardDescription className="text-xs text-slate-400 mt-2">
                    架构底座（React + TS + Tailwind + shadcn/ui）已完全就绪，后续业务模块可直接在此无缝拓展集成！
                  </CardDescription>
                </CardHeader>
              </Card>
            </div>
          )}
        </main>
      </div>
    </div>
  );
};

export default App;
