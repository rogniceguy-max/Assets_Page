import React, { useEffect, useRef, useState } from 'react';
import {
  AlertCircle,
  CheckCircle2,
  ChevronRight,
  CircleHelp,
  Download,
  File,
  Files,
  Folder,
  FolderOpen,
  HardDrive,
  LockKeyhole,
  LogOut,
  LoaderCircle,
  Pause,
  Play,
  RotateCcw,
  Upload,
  UserRound,
} from 'lucide-react';
import { Button } from '@/components/ui/button';

type WorkspaceView = 'files' | 'transfers';
type EntryKind = 'file' | 'directory';

interface FileEntry {
  name: string;
  kind: EntryKind;
  size: number;
  modified_at: string | number;
  read_only: boolean;
}

interface RawFileEntry {
  name: string;
  kind?: EntryKind;
  type?: string;
  size?: number;
  modified_at?: string | number;
  read_only?: boolean;
}

interface SessionUser {
  username: string;
  group_type: string;
}

interface FileWorkspaceProps {
  onLogout?: () => void;
}

interface SessionResponse {
  success?: boolean;
  authenticated?: boolean;
  user?: SessionUser;
  csrf_token?: string;
  error?: string;
}

interface FilesResponse {
  success?: boolean;
  path?: string;
  can_upload?: boolean;
  entries?: RawFileEntry[];
  error?: string;
}

interface BreadcrumbItem {
  label: string;
  path: string;
  readOnly: boolean;
}

type TransferKind = 'upload' | 'download';
type TransferStatus =
  | 'active'
  | 'pausing'
  | 'paused'
  | 'committing'
  | 'completed'
  | 'failed'
  | 'conflict';

interface TransferTask {
  id: string;
  kind: TransferKind;
  fileName: string;
  path: string;
  size: number;
  transferred: number;
  status: TransferStatus;
  error?: string;
}

interface WritableFileLike {
  seek(position: number): Promise<void>;
  truncate(size: number): Promise<void>;
  write(data: Uint8Array): Promise<void>;
  close(): Promise<void>;
}

interface TransferRuntime {
  paused: boolean;
  resumeWaiters: Array<() => void>;
  file?: File;
  uploadId?: string;
  offset: number;
  writer?: WritableFileLike;
}

interface UploadSessionResponse {
  success?: boolean;
  upload_id?: string;
  offset?: number;
  size?: number;
  error?: string;
}

interface UploadChunkResponse {
  success?: boolean;
  offset?: number;
  error?: string;
  code?: string;
}

interface SaveFileHandleLike {
  createWritable(): Promise<WritableFileLike>;
}

type WindowWithSavePicker = Window & {
  showSaveFilePicker?: (options: { suggestedName: string }) => Promise<SaveFileHandleLike>;
};

class FileApiError extends Error {
  status: number;
  code?: string;

  constructor(message: string, status: number, code?: string) {
    super(message);
    this.name = 'FileApiError';
    this.status = status;
    this.code = code;
  }
}

const PERSONAL_VIRTUAL_PATH = '/03_个人空间/我的空间';
const PERSONAL_INDEX_PATH = '/03_个人空间';
const PERSONAL_ALIAS_NAME = '我的空间';
const TRANSFER_CHUNK_BYTES = 4 * 1024 * 1024;

async function requestJson<T extends { success?: boolean; error?: string; code?: string }>(
  url: string,
  init?: RequestInit
): Promise<T> {
  const response = await fetch(url, { credentials: 'same-origin', ...init });
  let payload: T;
  try {
    payload = (await response.json()) as T;
  } catch {
    throw new FileApiError(`服务返回了无法读取的响应（${response.status}）。`, response.status);
  }

  if (!response.ok || payload.success === false) {
    const code = payload.code || (payload.error === 'FILE_EXISTS' ? 'FILE_EXISTS' : undefined);
    const message =
      code === 'FILE_EXISTS'
        ? '同名文件已存在，拒绝覆盖。'
        : payload.error || '文件请求失败。';
    throw new FileApiError(message, response.status, code);
  }
  return payload;
}

function uploadChunk(
  uploadId: string,
  csrfToken: string,
  start: number,
  end: number,
  total: number,
  chunk: Blob,
  onProgress: (loaded: number) => void
): Promise<number> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('PUT', `/api/uploads/${encodeURIComponent(uploadId)}`);
    xhr.setRequestHeader('Content-Type', 'application/octet-stream');
    xhr.setRequestHeader('Content-Range', `bytes ${start}-${end}/${total}`);
    if (csrfToken) xhr.setRequestHeader('X-CSRF-Token', csrfToken);
    xhr.upload.onprogress = (event) => {
      if (event.lengthComputable) onProgress(event.loaded);
    };
    xhr.onerror = () => reject(new Error('网络中断，分块未确认；可手动重试。'));
    xhr.onabort = () => reject(new Error('当前分块未确认。'));
    xhr.onload = () => {
      let payload: UploadChunkResponse;
      try {
        payload = JSON.parse(xhr.responseText) as UploadChunkResponse;
      } catch {
        reject(new FileApiError(`分块响应无法读取（${xhr.status}）。`, xhr.status));
        return;
      }

      if (xhr.status < 200 || xhr.status >= 300 || payload.success === false) {
        const code = payload.code || (payload.error === 'FILE_EXISTS' ? 'FILE_EXISTS' : undefined);
        reject(
          new FileApiError(
            code === 'FILE_EXISTS'
              ? '同名文件已存在，拒绝覆盖。'
              : payload.error || '分块上传失败。',
            xhr.status,
            code
          )
        );
        return;
      }
      if (!Number.isInteger(payload.offset)) {
        reject(new Error('服务端未返回已确认的分块位置。'));
        return;
      }
      resolve(payload.offset as number);
    };
    xhr.send(chunk);
  });
}

function formatBytes(value: number): string {
  if (!Number.isFinite(value) || value < 0) return '—';
  if (value < 1024) return `${value} B`;

  const units = ['KB', 'MB', 'GB', 'TB'];
  let size = value / 1024;
  let unitIndex = 0;
  while (size >= 1024 && unitIndex < units.length - 1) {
    size /= 1024;
    unitIndex += 1;
  }
  return `${size.toFixed(size >= 100 ? 0 : 1)} ${units[unitIndex]}`;
}

function formatModifiedAt(value: string | number): string {
  if (!value || value === 0) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date);
}

function groupLabel(groupType: string): string {
  if (groupType === 'film') return '影视组';
  if (groupType === 'writing') return '文学组';
  return groupType || '未分组';
}

function normalizeFileEntries(
  entries: RawFileEntry[],
  directoryPath: string,
  groupType: string,
  canUpload: boolean
): FileEntry[] {
  return entries.map((entry) => {
    const kind = entry.kind ?? (entry.type === 'directory' ? 'directory' : 'file');
    const isPersonalSpaceEntry =
      directoryPath === PERSONAL_INDEX_PATH && entry.name === PERSONAL_ALIAS_NAME;
    let readOnly = isPersonalSpaceEntry ? false : entry.read_only;

    if (readOnly === undefined) {
      if (directoryPath === '/' || directoryPath === '') {
        if (entry.name === '00_公共素材') readOnly = false;
        else if (entry.name === '01_影视资源库') readOnly = groupType !== 'film';
        else if (entry.name === '02_文学创作库') readOnly = groupType !== 'writing';
        else readOnly = true;
      } else {
        readOnly = !canUpload;
      }
    }

    return {
      name: entry.name,
      kind,
      size: Number.isFinite(entry.size) ? Number(entry.size) : 0,
      modified_at: entry.modified_at ?? '',
      read_only: readOnly,
    };
  });
}

function isPersonalPath(path: string): boolean {
  return path === PERSONAL_VIRTUAL_PATH || path.startsWith(`${PERSONAL_VIRTUAL_PATH}/`);
}

function displayPath(path: string): string {
  if (!path || path === '/') return '全部文件';
  if (path === PERSONAL_VIRTUAL_PATH) return '个人空间';
  if (path.startsWith(`${PERSONAL_VIRTUAL_PATH}/`)) {
    return `个人空间${path.slice(PERSONAL_VIRTUAL_PATH.length)}`;
  }
  return path.startsWith('/') ? path.slice(1) : path;
}

function transferStatusLabel(status: TransferStatus): string {
  if (status === 'active') return '进行中';
  if (status === 'pausing') return '当前分块完成后暂停';
  if (status === 'paused') return '已暂停';
  if (status === 'committing') return '正在提交';
  if (status === 'completed') return '已完成';
  if (status === 'conflict') return '拒绝覆盖';
  return '失败';
}

export const FileWorkspace: React.FC<FileWorkspaceProps> = ({ onLogout }) => {
  const [workspace, setWorkspace] = useState<WorkspaceView>('files');
  const [sessionUser, setSessionUser] = useState<SessionUser | null>(null);
  const [csrfToken, setCsrfToken] = useState('');
  const [sessionState, setSessionState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [logoutError, setLogoutError] = useState<string | null>(null);
  const [logoutLoading, setLogoutLoading] = useState(false);
  const [breadcrumbs, setBreadcrumbs] = useState<BreadcrumbItem[]>([]);
  const [entries, setEntries] = useState<FileEntry[]>([]);
  const [selectedEntry, setSelectedEntry] = useState<string | null>(null);
  const [directoryLoading, setDirectoryLoading] = useState(false);
  const [directoryError, setDirectoryError] = useState<string | null>(null);
  const [reloadToken, setReloadToken] = useState(0);
  const [transfers, setTransfers] = useState<TransferTask[]>([]);
  const directoryRequestId = useRef(0);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const transferRuntime = useRef(new Map<string, TransferRuntime>());

  const currentPath = breadcrumbs[breadcrumbs.length - 1]?.path ?? '/';
  const currentReadOnly = breadcrumbs[breadcrumbs.length - 1]?.readOnly ?? false;
  const currentPathRef = useRef(currentPath);
  currentPathRef.current = currentPath;

  useEffect(() => {
    let isCurrent = true;

    const loadSession = async () => {
      try {
        const response = await fetch('/api/session', { credentials: 'same-origin' });
        const data = (await response.json()) as SessionResponse;
        const hasSession = data.success === true || data.authenticated === true;
        if (!response.ok || !hasSession || !data.user?.username) {
          throw new Error(data.error || '未找到有效的本地模拟账号会话。');
        }
        if (!isCurrent) return;
        setSessionUser(data.user);
        setCsrfToken(data.csrf_token || '');
        setSessionState('ready');
      } catch (error) {
        if (!isCurrent) return;
        setSessionError(error instanceof Error ? error.message : '读取本地模拟账号失败。');
        setSessionState('error');
      }
    };

    void loadSession();
    return () => {
      isCurrent = false;
    };
  }, []);

  useEffect(() => {
    if (sessionState !== 'ready') return undefined;

    const requestId = ++directoryRequestId.current;
    const controller = new AbortController();
    const query = new URLSearchParams({ path: currentPath });

    setDirectoryLoading(true);
    setDirectoryError(null);

    const loadDirectory = async () => {
      try {
        const response = await fetch(`/api/files?${query.toString()}`, {
          credentials: 'same-origin',
          signal: controller.signal,
        });
        const data = (await response.json()) as FilesResponse;
        if (!response.ok || data.success === false) {
          throw new Error(data.error || '读取目录失败。');
        }
        if (requestId === directoryRequestId.current) {
          setEntries(
            Array.isArray(data.entries)
              ? normalizeFileEntries(data.entries, currentPath, sessionUser?.group_type || '', Boolean(data.can_upload))
              : []
          );
        }
      } catch (error) {
        if (controller.signal.aborted || requestId !== directoryRequestId.current) return;
        setEntries([]);
        setDirectoryError(error instanceof Error ? error.message : '读取目录失败。');
      } finally {
        if (requestId === directoryRequestId.current) setDirectoryLoading(false);
      }
    };

    void loadDirectory();
    return () => controller.abort();
  }, [currentPath, reloadToken, sessionState, sessionUser?.group_type]);

  const openDirectory = (entry: FileEntry) => {
    if (entry.kind !== 'directory') return;

    const nextPath = currentPath === '/' ? `/${entry.name}` : `${currentPath}/${entry.name}`;
    const nextReadOnly =
      nextPath === PERSONAL_VIRTUAL_PATH ? false : currentReadOnly || entry.read_only;
    setBreadcrumbs((current) => [
      ...current,
      {
        label: entry.name,
        path: nextPath,
        readOnly: nextReadOnly,
      },
    ]);
    setSelectedEntry(null);
  };

  const openPersonalSpace = () => {
    setBreadcrumbs([{ label: '个人空间', path: PERSONAL_VIRTUAL_PATH, readOnly: false }]);
    setSelectedEntry(null);
    setWorkspace('files');
  };

  const openRoot = () => {
    setBreadcrumbs([]);
    setSelectedEntry(null);
    setWorkspace('files');
  };

  const selectBreadcrumb = (index: number) => {
    setBreadcrumbs((current) => current.slice(0, index + 1));
    setSelectedEntry(null);
  };

  const canUploadHere = currentPath !== '/' && !currentReadOnly;
  const inPersonalSpace = isPersonalPath(currentPath);

  const setTransferStatus = (id: string, status: TransferStatus, error?: string) => {
    setTransfers((current) =>
      current.map((task) => (task.id === id ? { ...task, status, error } : task))
    );
  };

  const setTransferProgress = (id: string, transferred: number, size: number) => {
    setTransfers((current) =>
      current.map((task) =>
        task.id === id
          ? { ...task, transferred: Math.max(0, Math.min(transferred, size)) }
          : task
      )
    );
  };

  const waitForResume = async (id: string, runtime: TransferRuntime) => {
    if (!runtime.paused) return;
    setTransferStatus(id, 'paused');
    await new Promise<void>((resolve) => runtime.resumeWaiters.push(resolve));
    setTransferStatus(id, 'active');
  };

  const runUpload = async (taskId: string, targetPath: string) => {
    const runtime = transferRuntime.current.get(taskId);
    if (!runtime?.file) return;
    const file = runtime.file;

    try {
      if (!runtime.uploadId) {
        const created = await requestJson<UploadSessionResponse>('/api/uploads', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            ...(csrfToken ? { 'X-CSRF-Token': csrfToken } : {}),
          },
          body: JSON.stringify({ path: targetPath, filename: file.name, size: file.size }),
        });
        if (!created.upload_id) throw new Error('服务端未返回上传会话编号。');
        runtime.uploadId = created.upload_id;
      }
      const uploadId = runtime.uploadId;
      if (!uploadId) throw new Error('上传会话不可用。');

      const session = await requestJson<UploadSessionResponse>(
        `/api/uploads/${encodeURIComponent(uploadId)}`
      );
      if (session.size !== file.size) {
        throw new Error('当前选择的文件大小与上传会话不一致，无法安全续传。');
      }
      if (!Number.isInteger(session.offset) || (session.offset ?? -1) < 0 || (session.offset ?? 0) > file.size) {
        throw new Error('服务端返回的续传位置无效。');
      }

      let offset = session.offset as number;
      runtime.offset = offset;
      setTransferProgress(taskId, offset, file.size);

      while (offset < file.size) {
        await waitForResume(taskId, runtime);
        const end = Math.min(offset + TRANSFER_CHUNK_BYTES, file.size) - 1;
        const chunk = file.slice(offset, end + 1);
        const nextOffset = await uploadChunk(
          uploadId,
          csrfToken,
          offset,
          end,
          file.size,
          chunk,
          (loaded) => setTransferProgress(taskId, offset + loaded, file.size)
        );
        if (nextOffset !== end + 1) {
          throw new Error('服务端确认的续传位置与已发送分块不一致。');
        }
        offset = nextOffset;
        runtime.offset = offset;
        setTransferProgress(taskId, offset, file.size);
      }

      await waitForResume(taskId, runtime);
      setTransferStatus(taskId, 'committing');
      await requestJson<{ success: boolean; error?: string }>(
        '/api/uploads/' + encodeURIComponent(uploadId) + '/complete',
        {
          method: 'POST',
          headers: csrfToken ? { 'X-CSRF-Token': csrfToken } : undefined,
        }
      );
      setTransferProgress(taskId, file.size, file.size);
      setTransferStatus(taskId, 'completed');
      transferRuntime.current.delete(taskId);
      if (currentPathRef.current === targetPath) setReloadToken((token) => token + 1);
    } catch (error) {
      const message = error instanceof Error ? error.message : '上传失败。';
      if (error instanceof FileApiError && error.code === 'FILE_EXISTS') {
        transferRuntime.current.delete(taskId);
        setTransferStatus(taskId, 'conflict', '同名文件已存在，拒绝覆盖。');
      } else {
        setTransferProgress(taskId, runtime.offset, file.size);
        if (runtime.uploadId) {
          try {
            const session = await requestJson<UploadSessionResponse>(
              `/api/uploads/${encodeURIComponent(runtime.uploadId)}`
            );
            if (
              Number.isInteger(session.offset) &&
              session.size === file.size &&
              (session.offset ?? -1) >= 0 &&
              (session.offset ?? file.size + 1) <= file.size
            ) {
              runtime.offset = session.offset as number;
              setTransferProgress(taskId, runtime.offset, file.size);
            }
          } catch {
            // Keep the last server-confirmed chunk position if status is unavailable.
          }
        }
        setTransferStatus(taskId, 'failed', message);
      }
    }
  };

  const runDownload = async (taskId: string, filePath: string, fileName: string, fileSize: number) => {
    const runtime = transferRuntime.current.get(taskId);
    if (!runtime) return;

    try {
      const pickerWindow = window as WindowWithSavePicker;
      if (!pickerWindow.showSaveFilePicker) {
        throw new Error('此环境不支持流式保存下载；为避免把大文件读入内存，已停止下载。');
      }

      if (!runtime.writer) {
        const handle = await pickerWindow.showSaveFilePicker({ suggestedName: fileName });
        runtime.writer = await handle.createWritable();
        await runtime.writer.truncate(0);
        runtime.offset = 0;
      } else {
        await runtime.writer.seek(runtime.offset);
        await runtime.writer.truncate(runtime.offset);
      }
      const writer = runtime.writer;
      if (!writer) throw new Error('无法打开本地下载目标。');

      const query = new URLSearchParams({ path: filePath });
      const downloadUrl = `/api/files/download?${query.toString()}`;

      if (fileSize === 0) {
        await waitForResume(taskId, runtime);
        const response = await fetch(downloadUrl, { credentials: 'same-origin' });
        if (!response.ok) throw new FileApiError('下载请求失败。', response.status);
        if (!response.body) throw new Error('浏览器未提供可流式读取的下载内容。');
        const reader = response.body.getReader();
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          if (value) await writer.write(value);
        }
      } else {
        while (runtime.offset < fileSize) {
          await waitForResume(taskId, runtime);
          const start = runtime.offset;
          const end = Math.min(start + TRANSFER_CHUNK_BYTES, fileSize) - 1;
          const response = await fetch(downloadUrl, {
            credentials: 'same-origin',
            headers: { Range: `bytes=${start}-${end}` },
          });
          if (response.status !== 206) {
            await response.body?.cancel();
            throw new Error('服务端未返回可续传的 206 分块响应，已停止下载。');
          }

          const range = response.headers.get('Content-Range') || '';
          const match = /^bytes (\d+)-(\d+)\/(\d+)$/i.exec(range);
          if (!match) {
            await response.body?.cancel();
            throw new Error('下载分块缺少有效的 Content-Range。');
          }
          const responseStart = Number(match[1]);
          const responseEnd = Number(match[2]);
          const responseSize = Number(match[3]);
          if (responseStart !== start || responseEnd < start || responseEnd > end || responseSize !== fileSize) {
            await response.body?.cancel();
            throw new Error('下载分块范围与文件信息不一致，已停止下载。');
          }
          if (!response.body) throw new Error('浏览器未提供可流式读取的下载内容。');

          const expectedBytes = responseEnd - responseStart + 1;
          const reader = response.body.getReader();
          let writtenBytes = 0;
          await writer.seek(start);
          while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            if (!value) continue;
            await writer.write(value);
            writtenBytes += value.byteLength;
            setTransferProgress(taskId, start + writtenBytes, fileSize);
          }
          if (writtenBytes !== expectedBytes) {
            throw new Error('下载分块长度不完整；可手动重试已确认部分。');
          }

          runtime.offset = responseEnd + 1;
          setTransferProgress(taskId, runtime.offset, fileSize);
        }
      }

      await waitForResume(taskId, runtime);
      setTransferStatus(taskId, 'committing');
      await writer.close();
      runtime.writer = undefined;
      setTransferProgress(taskId, fileSize, fileSize);
      setTransferStatus(taskId, 'completed');
      transferRuntime.current.delete(taskId);
    } catch (error) {
      const message = error instanceof Error ? error.message : '下载失败。';
      setTransferProgress(taskId, runtime.offset, fileSize);
      setTransferStatus(taskId, 'failed', message);
    }
  };

  const beginUpload = (file: File) => {
    const targetPath = currentPath;
    const duplicateInDirectory = entries.some((entry) => entry.name === file.name);
    const duplicateTask = transfers.some(
      (task) => task.kind === 'upload' && task.path === targetPath && task.fileName === file.name
    );
    const conflict = duplicateInDirectory || duplicateTask;
    const taskId = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
    const task: TransferTask = {
      id: taskId,
      kind: 'upload',
      fileName: file.name,
      path: targetPath,
      size: file.size,
      transferred: 0,
      status: conflict ? 'conflict' : 'active',
      error: conflict ? '同名文件已存在，拒绝覆盖。' : undefined,
    };
    if (!conflict) {
      transferRuntime.current.set(taskId, { paused: false, resumeWaiters: [], file, offset: 0 });
    }
    setTransfers((current) => [task, ...current]);
    setWorkspace('transfers');
    if (!conflict) void runUpload(taskId, targetPath);
  };

  const beginDownload = (entry: FileEntry) => {
    const filePath = `${currentPath === '/' ? '' : currentPath}/${entry.name}`;
    const taskId = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`;
    const task: TransferTask = {
      id: taskId,
      kind: 'download',
      fileName: entry.name,
      path: filePath,
      size: entry.size,
      transferred: 0,
      status: 'active',
    };
    transferRuntime.current.set(taskId, { paused: false, resumeWaiters: [], offset: 0 });
    setTransfers((current) => [task, ...current]);
    setWorkspace('transfers');
    void runDownload(taskId, filePath, entry.name, entry.size);
  };

  const handleFileSelected = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.currentTarget.files?.[0];
    event.currentTarget.value = '';
    if (!file) return;
    if (!canUploadHere) {
      setDirectoryError(currentReadOnly ? '此目录只读，不能上传。' : '请先进入可写目录。');
      return;
    }
    beginUpload(file);
  };

  const pauseTransfer = (taskId: string) => {
    const runtime = transferRuntime.current.get(taskId);
    if (!runtime || runtime.paused) return;
    runtime.paused = true;
    setTransferStatus(taskId, 'pausing');
  };

  const resumeTransfer = (taskId: string) => {
    const runtime = transferRuntime.current.get(taskId);
    if (!runtime) return;
    runtime.paused = false;
    runtime.resumeWaiters.splice(0).forEach((resolve) => resolve());
    setTransferStatus(taskId, 'active');
  };

  const retryTransfer = (task: TransferTask) => {
    const runtime = transferRuntime.current.get(task.id);
    if (!runtime || task.status !== 'failed') return;
    runtime.paused = false;
    runtime.resumeWaiters.splice(0).forEach((resolve) => resolve());
    setTransferStatus(task.id, 'active');
    if (task.kind === 'upload') {
      void runUpload(task.id, task.path);
    } else {
      void runDownload(task.id, task.path, task.fileName, task.size);
    }
  };

  const handleLogout = async () => {
    if (logoutLoading) return;
    const hasUnfinishedTransfers = transfers.some((task) => task.status !== 'completed');
    if (
      hasUnfinishedTransfers &&
      !window.confirm('退出会清除未完成的传输任务，之后无法继续。仍要退出吗？')
    ) {
      return;
    }
    setLogoutError(null);
    if (!csrfToken) {
      setLogoutError('当前会话未提供 CSRF token，无法安全退出。');
      return;
    }

    setLogoutLoading(true);
    try {
      await requestJson<{ success: boolean; error?: string }>('/api/logout', {
        method: 'POST',
        headers: { 'X-CSRF-Token': csrfToken },
      });
      setSessionUser(null);
      setSessionState('error');
      setSessionError('已退出登录，请重新登录后继续。');
      onLogout?.();
    } catch (error) {
      setLogoutError(error instanceof Error ? error.message : '退出登录失败。');
    } finally {
      setLogoutLoading(false);
    }
  };

  return (
    <section className="relative flex h-full min-h-[560px] w-full min-w-0 flex-col overflow-hidden rounded-xl border border-white/10 bg-slate-950/90 text-slate-100 shadow-2xl">
      <header className="flex flex-wrap items-center justify-between gap-4 border-b border-white/10 px-5 py-4">
        <div className="flex min-w-0 items-center gap-3">
          <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg border border-sky-400/20 bg-sky-400/10 text-sky-300">
            <HardDrive className="h-5 w-5" aria-hidden="true" />
          </div>
          <div className="min-w-0">
            <h1 className="truncate text-base font-semibold text-white">文件工作区</h1>
            <p className="mt-0.5 text-xs text-slate-400">本地模拟环境 · 使用本地 API，不连接 NAS</p>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-3">
          {sessionUser && (
            <div className="flex items-center gap-2 rounded-lg border border-white/10 bg-white/[0.03] px-3 py-2 text-xs text-slate-300">
              <UserRound className="h-4 w-4 text-slate-400" aria-hidden="true" />
              <span className="max-w-36 truncate" title={sessionUser.username}>
                {sessionUser.username}
              </span>
              <span className="text-slate-500">·</span>
              <span>{groupLabel(sessionUser.group_type)}</span>
            </div>
          )}

          <nav className="flex rounded-lg border border-white/10 bg-slate-900 p-1" aria-label="工作区">
            <button
              type="button"
              onClick={() => setWorkspace('files')}
              aria-current={workspace === 'files' ? 'page' : undefined}
              className={`flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors ${
                workspace === 'files'
                  ? 'bg-slate-700 text-white shadow-sm'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              <Files className="h-4 w-4" aria-hidden="true" />
              文件
            </button>
            <button
              type="button"
              onClick={() => setWorkspace('transfers')}
              aria-current={workspace === 'transfers' ? 'page' : undefined}
              className={`flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors ${
                workspace === 'transfers'
                  ? 'bg-slate-700 text-white shadow-sm'
                  : 'text-slate-400 hover:text-slate-200'
              }`}
            >
              <Upload className="h-4 w-4" aria-hidden="true" />
              传输
            </button>
          </nav>
          <Button
            type="button"
            variant="ghost"
            disabled={logoutLoading || sessionState !== 'ready'}
            onClick={() => void handleLogout()}
            className="h-9 gap-2 px-3 text-xs text-slate-300 hover:text-white"
          >
            {logoutLoading ? (
              <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" />
            ) : (
              <LogOut className="h-4 w-4" aria-hidden="true" />
            )}
            退出
          </Button>
        </div>
      </header>

      {logoutError && (
        <div role="alert" className="border-b border-red-400/20 bg-red-400/10 px-5 py-2 text-xs text-red-200">
          {logoutError}
        </div>
      )}

      <div className="flex min-h-0 flex-1">
        <aside className="hidden w-56 shrink-0 flex-col border-r border-white/10 bg-slate-900/50 p-3 sm:flex">
          <p className="px-3 pb-2 pt-1 text-[11px] font-semibold uppercase tracking-[0.12em] text-slate-500">
            空间
          </p>
          <button
            type="button"
            onClick={openRoot}
            aria-current={breadcrumbs.length === 0 && workspace === 'files' ? 'page' : undefined}
            className={`flex items-center gap-2 rounded-lg px-3 py-2.5 text-left text-sm transition-colors ${
              breadcrumbs.length === 0 && workspace === 'files'
                ? 'bg-sky-400/10 text-sky-200'
                : 'text-slate-300 hover:bg-white/[0.04] hover:text-white'
            }`}
          >
            <FolderOpen className="h-4 w-4" aria-hidden="true" />
            全部文件
          </button>
          <button
            type="button"
            onClick={openPersonalSpace}
            aria-current={inPersonalSpace && workspace === 'files' ? 'page' : undefined}
            className={`mt-1 flex items-center gap-2 rounded-lg px-3 py-2.5 text-left text-sm transition-colors ${
              inPersonalSpace && workspace === 'files'
                ? 'bg-sky-400/10 text-sky-200'
                : 'text-slate-300 hover:bg-white/[0.04] hover:text-white'
            }`}
          >
            <UserRound className="h-4 w-4" aria-hidden="true" />
            个人空间
          </button>

          <div className="mt-auto rounded-lg border border-white/10 bg-slate-950/60 p-3">
            <div className="flex items-center gap-2 text-xs font-medium text-slate-300">
              <CircleHelp className="h-4 w-4 text-slate-500" aria-hidden="true" />
              目录权限
            </div>
            <p className="mt-2 text-[11px] leading-relaxed text-slate-500">
              只读标记来自本地接口。当前演示不代表实际 NAS ACL。
            </p>
          </div>
        </aside>

        <main className="flex min-w-0 flex-1 flex-col overflow-hidden">
          {workspace === 'files' ? (
            <div className="flex min-h-0 flex-1 flex-col">
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-white/10 px-5 py-4">
                <div className="min-w-0 flex-1">
                  <nav aria-label="文件路径" className="flex min-w-0 items-center gap-1 overflow-x-auto text-sm">
                    <button
                      type="button"
                      onClick={openRoot}
                      className={`shrink-0 rounded px-1 py-1 transition-colors ${
                        breadcrumbs.length === 0 ? 'font-medium text-white' : 'text-slate-400 hover:text-white'
                      }`}
                    >
                      全部文件
                    </button>
                    {breadcrumbs.map((crumb, index) => (
                      <React.Fragment key={`${crumb.path}-${index}`}>
                        <ChevronRight className="h-4 w-4 shrink-0 text-slate-600" aria-hidden="true" />
                        <button
                          type="button"
                          onClick={() => selectBreadcrumb(index)}
                          aria-current={index === breadcrumbs.length - 1 ? 'page' : undefined}
                          className={`max-w-48 truncate rounded px-1 py-1 transition-colors ${
                            index === breadcrumbs.length - 1
                              ? 'font-medium text-white'
                              : 'text-slate-400 hover:text-white'
                          }`}
                          title={crumb.label}
                        >
                          {crumb.label}
                        </button>
                      </React.Fragment>
                    ))}
                  </nav>
                  <p className="mt-1 truncate pl-1 text-xs text-slate-500">
                    {currentReadOnly
                      ? '此目录只读'
                      : currentPath !== '/'
                        ? '当前目录可写'
                        : '根目录内容由本地接口提供'}
                  </p>
                </div>

                <div className="flex items-center gap-2">
                  <input
                    ref={fileInputRef}
                    type="file"
                    className="hidden"
                    aria-label="选择单个文件上传"
                    onChange={handleFileSelected}
                  />
                  <Button
                    type="button"
                    variant="outline"
                    disabled={!canUploadHere || sessionState !== 'ready' || directoryLoading}
                    title={canUploadHere ? '上传单个文件' : '请进入可写目录'}
                    onClick={() => fileInputRef.current?.click()}
                    className="h-9 gap-2 border-white/10 px-3 text-xs"
                  >
                    <Upload className="h-4 w-4" aria-hidden="true" />
                    上传单个文件
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    disabled={directoryLoading || sessionState !== 'ready'}
                    onClick={() => setReloadToken((token) => token + 1)}
                    className="h-9 px-3 text-xs"
                  >
                    {directoryLoading ? (
                      <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" />
                    ) : (
                      '刷新'
                    )}
                  </Button>
                </div>
              </div>

              {directoryError && (
                <div className="mx-5 mt-4 flex items-start gap-2 rounded-lg border border-red-400/20 bg-red-400/10 px-3 py-2.5 text-sm text-red-200">
                  <AlertCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
                  <span>{directoryError}</span>
                </div>
              )}

              <div className="min-h-0 flex-1 overflow-auto p-5">
                {sessionState === 'loading' || directoryLoading ? (
                  <div className="flex h-40 items-center justify-center gap-2 text-sm text-slate-400">
                    <LoaderCircle className="h-4 w-4 animate-spin" aria-hidden="true" />
                    正在读取目录…
                  </div>
                ) : directoryError ? null : entries.length === 0 ? (
                  <div className="flex min-h-56 flex-col items-center justify-center rounded-xl border border-dashed border-white/10 bg-white/[0.02] px-6 text-center">
                    <Folder className="h-8 w-8 text-slate-600" aria-hidden="true" />
                    <p className="mt-3 text-sm font-medium text-slate-300">此处暂时没有文件</p>
                    <p className="mt-1 text-xs text-slate-500">文件列表由本地接口提供。</p>
                  </div>
                ) : (
                  <div className="overflow-x-auto rounded-xl border border-white/10 bg-slate-900/40">
                    <table className="w-full min-w-[640px] border-collapse text-left text-sm">
                      <thead className="bg-white/[0.025] text-xs text-slate-500">
                        <tr>
                          <th scope="col" className="px-4 py-3 font-medium">名称</th>
                          <th scope="col" className="w-32 px-4 py-3 font-medium">大小</th>
                          <th scope="col" className="w-48 px-4 py-3 font-medium">修改时间</th>
                          <th scope="col" className="w-28 px-4 py-3 font-medium">权限</th>
                          <th scope="col" className="w-28 px-4 py-3 font-medium">操作</th>
                        </tr>
                      </thead>
                      <tbody>
                        {entries.map((entry) => {
                          const selected = selectedEntry === entry.name;
                          return (
                            <tr
                              key={`${entry.kind}:${entry.name}`}
                              aria-selected={selected}
                              onClick={() => setSelectedEntry(entry.name)}
                              onDoubleClick={() => openDirectory(entry)}
                              className={`border-t border-white/[0.06] transition-colors ${
                                selected ? 'bg-sky-400/[0.08]' : 'hover:bg-white/[0.035]'
                              }`}
                            >
                              <td className="max-w-0 px-4 py-3">
                                <div className="flex min-w-0 items-center gap-3">
                                  {entry.kind === 'directory' ? (
                                    <Folder className="h-4 w-4 shrink-0 text-sky-300" aria-hidden="true" />
                                  ) : (
                                    <File className="h-4 w-4 shrink-0 text-slate-400" aria-hidden="true" />
                                  )}
                                  <button
                                    type="button"
                                    onClick={(event) => {
                                      event.stopPropagation();
                                      setSelectedEntry(entry.name);
                                    }}
                                    onDoubleClick={(event) => {
                                      event.stopPropagation();
                                      openDirectory(entry);
                                    }}
                                    className={`min-w-0 truncate text-left ${
                                      selected ? 'text-sky-100' : 'text-slate-200 hover:text-white'
                                    }`}
                                    title={entry.name}
                                  >
                                    {entry.name}
                                  </button>
                                </div>
                              </td>
                              <td className="px-4 py-3 text-xs tabular-nums text-slate-400">
                                {entry.kind === 'directory' ? '—' : formatBytes(entry.size)}
                              </td>
                              <td className="px-4 py-3 text-xs tabular-nums text-slate-400">
                                {formatModifiedAt(entry.modified_at)}
                              </td>
                              <td className="px-4 py-3">
                                <span
                                  className={`inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-[11px] ${
                                    entry.read_only
                                      ? 'bg-slate-700/70 text-slate-300'
                                      : 'bg-emerald-400/10 text-emerald-200'
                                  }`}
                                >
                                  {entry.read_only && <LockKeyhole className="h-3 w-3" aria-hidden="true" />}
                                  {entry.read_only ? '只读' : '可写'}
                                </span>
                              </td>
                              <td className="px-4 py-3">
                                {entry.kind === 'file' ? (
                                  <button
                                    type="button"
                                    onClick={(event) => {
                                      event.stopPropagation();
                                      beginDownload(entry);
                                    }}
                                    title="下载单个文件"
                                    className="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-slate-300 transition-colors hover:bg-white/[0.06] hover:text-white"
                                  >
                                    <Download className="h-3.5 w-3.5" aria-hidden="true" />
                                    下载
                                  </button>
                                ) : (
                                  <span className="text-xs text-slate-600">—</span>
                                )}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            </div>
          ) : (
            <div className="flex min-h-0 flex-1 flex-col p-5 md:p-6">
              <div className="mb-4 flex items-center gap-3">
                <div className="flex h-10 w-10 items-center justify-center rounded-lg border border-slate-500/20 bg-slate-500/10 text-slate-300">
                  <Upload className="h-5 w-5" aria-hidden="true" />
                </div>
                <div>
                  <h2 className="text-base font-semibold text-white">传输</h2>
                  <p className="mt-0.5 text-xs text-slate-500">本地模拟服务 · 单文件任务 · 切换工作区时保留进度</p>
                </div>
              </div>
              {transfers.length === 0 ? (
                <div className="flex min-h-56 flex-1 flex-col items-center justify-center rounded-xl border border-dashed border-white/10 bg-white/[0.02] px-6 text-center">
                  <CircleHelp className="h-8 w-8 text-slate-600" aria-hidden="true" />
                  <p className="mt-3 text-sm font-medium text-slate-300">暂无传输任务</p>
                  <p className="mt-1 max-w-md text-xs leading-relaxed text-slate-500">
                    从文件区添加单文件上传或下载任务。文件传输由本地 API 处理，不连接 NAS。
                  </p>
                </div>
              ) : (
                <div className="min-h-0 flex-1 space-y-3 overflow-auto">
                  {transfers.map((task) => {
                    const progress =
                      task.size === 0
                        ? task.status === 'completed'
                          ? 100
                          : 0
                        : Math.min(100, Math.floor((task.transferred / task.size) * 100));
                    const statusClass =
                      task.status === 'completed'
                        ? 'bg-emerald-400/10 text-emerald-200'
                        : task.status === 'failed' || task.status === 'conflict'
                          ? 'bg-red-400/10 text-red-200'
                          : task.status === 'paused' || task.status === 'pausing'
                            ? 'bg-amber-400/10 text-amber-200'
                            : 'bg-sky-400/10 text-sky-200';

                    return (
                      <article key={task.id} className="rounded-xl border border-white/10 bg-slate-900/50 p-4">
                        <div className="flex flex-wrap items-start justify-between gap-3">
                          <div className="flex min-w-0 items-start gap-3">
                            <div className="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-white/[0.04] text-slate-300">
                              {task.kind === 'upload' ? (
                                <Upload className="h-4 w-4" aria-hidden="true" />
                              ) : (
                                <Download className="h-4 w-4" aria-hidden="true" />
                              )}
                            </div>
                            <div className="min-w-0">
                              <h3 className="truncate text-sm font-medium text-slate-100" title={task.fileName}>
                                {task.fileName}
                              </h3>
                              <p className="mt-1 truncate text-xs text-slate-500" title={displayPath(task.path)}>
                                {task.kind === 'upload' ? '上传到 ' : '下载自 '}
                                {displayPath(task.path)}
                                <span className="px-1.5">·</span>
                                {formatBytes(task.size)}
                              </p>
                            </div>
                          </div>
                          <div className="flex shrink-0 items-center gap-2">
                            <span className={`rounded-md px-2 py-1 text-[11px] ${statusClass}`}>
                              {transferStatusLabel(task.status)}
                            </span>
                            {task.status === 'active' && (
                              <button
                                type="button"
                                onClick={() => pauseTransfer(task.id)}
                                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-white/10 px-2.5 text-xs text-slate-300 hover:bg-white/[0.05]"
                              >
                                <Pause className="h-3.5 w-3.5" aria-hidden="true" />
                                暂停
                              </button>
                            )}
                            {task.status === 'paused' && (
                              <button
                                type="button"
                                onClick={() => resumeTransfer(task.id)}
                                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-white/10 px-2.5 text-xs text-slate-300 hover:bg-white/[0.05]"
                              >
                                <Play className="h-3.5 w-3.5" aria-hidden="true" />
                                继续
                              </button>
                            )}
                            {task.status === 'failed' && (
                              <button
                                type="button"
                                onClick={() => retryTransfer(task)}
                                className="inline-flex h-8 items-center gap-1.5 rounded-md border border-white/10 px-2.5 text-xs text-slate-300 hover:bg-white/[0.05]"
                              >
                                <RotateCcw className="h-3.5 w-3.5" aria-hidden="true" />
                                重试
                              </button>
                            )}
                          </div>
                        </div>

                        <div className="mt-4 flex items-center gap-3">
                          <div
                            className="h-1.5 flex-1 overflow-hidden rounded-full bg-slate-800"
                            role="progressbar"
                            aria-valuemin={0}
                            aria-valuemax={100}
                            aria-valuenow={progress}
                            aria-label={`${task.fileName} 传输进度`}
                          >
                            <div
                              className={`h-full rounded-full transition-[width] duration-150 ${
                                task.status === 'failed' || task.status === 'conflict'
                                  ? 'bg-red-400'
                                  : task.status === 'completed'
                                    ? 'bg-emerald-400'
                                  : task.status === 'paused' || task.status === 'pausing'
                                      ? 'bg-amber-400'
                                      : 'bg-sky-400'
                              }`}
                              style={{ width: `${progress}%` }}
                            />
                          </div>
                          <span className="w-28 text-right text-xs tabular-nums text-slate-400">
                            {formatBytes(task.transferred)} / {formatBytes(task.size)}
                          </span>
                        </div>
                        {task.error && <p className="mt-2 text-xs text-red-300">{task.error}</p>}
                        {task.status === 'completed' && (
                          <p className="mt-2 flex items-center gap-1.5 text-xs text-emerald-200/80">
                            <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                            {task.kind === 'upload' ? '服务端已完成文件提交。' : '文件已流式保存到所选位置。'}
                          </p>
                        )}
                      </article>
                    );
                  })}
                </div>
              )}
            </div>
          )}
        </main>
      </div>

      {sessionState === 'error' && (
        <div className="absolute inset-0 z-20 flex items-center justify-center bg-slate-950/90 p-6">
          <div className="w-full max-w-md rounded-xl border border-red-400/20 bg-slate-900 p-5 shadow-2xl">
            <div className="flex items-start gap-3">
              <AlertCircle className="mt-0.5 h-5 w-5 shrink-0 text-red-300" aria-hidden="true" />
              <div>
                <h2 className="text-sm font-semibold text-white">
                  {sessionError?.startsWith('已退出登录') ? '会话已退出' : '无法读取本地模拟会话'}
                </h2>
                <p className="mt-1 text-sm leading-relaxed text-slate-400">
                  {sessionError || '请先完成本地模拟登录。'}
                </p>
              </div>
            </div>
          </div>
        </div>
      )}
    </section>
  );
};

export default FileWorkspace;
