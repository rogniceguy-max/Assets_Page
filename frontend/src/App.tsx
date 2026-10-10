import React, { useState } from 'react';
import { Header } from '@/components/layout/Header';
import { Sidebar } from '@/components/layout/Sidebar';
import { RegisterCard, AuthTab, AuthSuccessData } from '@/components/registration/RegisterCard';
import { SuccessCard } from '@/components/registration/SuccessCard';
import { UserDashboard } from '@/components/dashboard/UserDashboard';
import { Video, PenTool, FolderTree, Users } from 'lucide-react';
import { Card, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';

interface CurrentUser {
  fullname: string;
  linux_username: string;
  group_type: string;
  token?: string;
}

export const App: React.FC = () => {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [activeNav, setActiveNav] = useState('onboarding');
  
  // 当前已登录用户状态
  const [currentUser, setCurrentUser] = useState<CurrentUser | null>(null);
  
  // 当前挂载会话（用于下载 Windows 原生挂载工具）
  const [mountSession, setMountSession] = useState<{ fullname: string; linuxUsername?: string } | null>(null);

  // 认证卡片默认 Tab
  const [authDefaultTab, setAuthDefaultTab] = useState<AuthTab>('login');

  const handleAuthSuccess = (data: AuthSuccessData) => {
    const user: CurrentUser = {
      fullname: data.fullname,
      linux_username: data.linux_username || data.fullname,
      group_type: data.group_type || 'film',
    };
    setCurrentUser(user);

    if (data.mode === 'register' || data.mode === 'remount') {
      // 注册或重新挂载流程：直接调起挂载工具下载卡片
      setMountSession({ fullname: data.fullname, linuxUsername: data.linux_username });
    } else {
      // 纯登录流程：进入员工工作台
      setMountSession(null);
    }
  };

  const handleLogout = () => {
    setCurrentUser(null);
    setMountSession(null);
    setAuthDefaultTab('login');
  };

  const handleStartMount = () => {
    if (currentUser) {
      setMountSession({ fullname: currentUser.fullname, linuxUsername: currentUser.linux_username });
    } else {
      // 若无登录态，引导到重新挂载标签
      setAuthDefaultTab('remount');
      setCurrentUser(null);
      setMountSession(null);
    }
  };

  return (
    <div className="flex flex-col h-screen overflow-hidden bg-background text-foreground font-sans">
      {/* 顶部工具栏 (56–64px) */}
      <Header
        sidebarOpen={sidebarOpen}
        setSidebarOpen={setSidebarOpen}
        currentUser={currentUser}
        onLogout={handleLogout}
      />

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
            mountSession ? (
              /* 1. Windows 原生凭据挂载工具下载状态 */
              <SuccessCard
                fullname={mountSession.fullname}
                linuxUsername={mountSession.linuxUsername}
                onBack={() => setMountSession(null)}
              />
            ) : currentUser ? (
              /* 2. 登录后的员工工作台主视图 */
              <UserDashboard
                user={currentUser}
                onMount={handleStartMount}
                onLogout={handleLogout}
              />
            ) : (
              /* 3. 登录 / 注册 / 重新挂载 三合一入口卡片 */
              <RegisterCard
                defaultTab={authDefaultTab}
                onSuccess={handleAuthSuccess}
              />
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
