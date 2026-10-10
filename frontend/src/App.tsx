import React, { useState } from 'react';
import { Header } from '@/components/layout/Header';
import { Sidebar } from '@/components/layout/Sidebar';
import { RegisterCard } from '@/components/registration/RegisterCard';
import { SuccessCard } from '@/components/registration/SuccessCard';
import { Video, PenTool, FolderTree, Users } from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';

export const App: React.FC = () => {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [activeNav, setActiveNav] = useState('onboarding');
  const [registeredUser, setRegisteredUser] = useState<string | null>(null);

  return (
    <div className="flex flex-col h-screen overflow-hidden bg-background text-foreground font-sans">
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

        {/* 主内容区域：纯净低刺激暗室工作底色，无刺眼高饱和光斑 */}
        <main className="flex-1 overflow-y-auto p-4 md:p-8 flex items-center justify-center relative">
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
              <Card className="p-8">
                <CardHeader>
                  <div className="flex justify-center mb-3">
                    <div className="h-10 w-10 rounded-[8px] bg-secondary border border-border flex items-center justify-center text-muted">
                      {activeNav === 'film' && <Video className="h-5 w-5" />}
                      {activeNav === 'writing' && <PenTool className="h-5 w-5" />}
                      {activeNav === 'assets' && <FolderTree className="h-5 w-5" />}
                      {activeNav === 'members' && <Users className="h-5 w-5" />}
                    </div>
                  </div>
                  <CardTitle className="text-lg font-semibold text-foreground">
                    {activeNav === 'film' && '01_影视资源库 (在线素材流)'}
                    {activeNav === 'writing' && '02_文学创作库 (剧本大纲协作)'}
                    {activeNav === 'assets' && '00_公共素材资产总库'}
                    {activeNav === 'members' && '团队成员与目录权限管理中心'}
                  </CardTitle>
                  <CardDescription className="text-xs text-muted mt-2">
                    该模块正在系统规划排期中。请先前往【挂载与入职】完成身份登记并领取本地挂载脚本。
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
