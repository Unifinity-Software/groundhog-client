/** Public, credential-free shapes from docs/ide-protocol.md. */
export interface Workspace { slug: string; name: string; role: string }
export interface Tunnel { workspace: string; name: string; url: string; state: string; location?: 'local' | 'remote' | 'offline'; actions: string[]; hasKey: boolean; captureBodies?: boolean; canCaptureBodies?: boolean }
export interface Agent { name: string; target: string; url?: string; state: string; source: 'cli' | 'aspire'; processId: number }
export interface AuthStatus { state: 'signedIn' | 'signedOut' | 'expired'; workspace?: string | null; expiresAt?: string | null }
export interface RequestSummary { requestId: string; completed: string; method: string; path: string; statusCode: number; durationMs: number; source: 'local' | 'remote' }
export interface TunnelRef { workspace: string; name: string }
export type Empty = Record<string, never>;
export interface InitializeResult { protocolVersion: number; cliVersion: string; capabilities: string[] }
export interface RequestDetail { requestId: string; method: string; path: string; headers: Record<string, string>; body: string; truncated: boolean; captureBodies: boolean; canCaptureBodies: boolean; responseHeaders?: Record<string, string>; responseBody?: string; responseTruncated?: boolean; statusCode?: number }
export interface VisitorAccess { default: { allow: string[] } | null; rules: { path: string; allow: string[] }[]; ipRanges: string[]; apiKeys: { id: string; name: string; created: string }[]; managedBy: 'dashboard' | 'agent' }
export interface RequestRef extends TunnelRef { requestId: string }
export interface Methods {
  initialize: [{ clientVersion: string; protocolVersions: number[] }, InitializeResult];
  'auth/status': [Empty, AuthStatus];
  'auth/login': [Empty, { userCode: string; verificationUri: string; expiresAt: string }];
  'auth/logout': [Empty, Empty];
  'workspaces/list': [Empty, { workspaces: Workspace[]; selected: string | null }];
  'workspaces/use': [{ workspace: string }, { workspace: string }];
  'tunnels/list': [{ workspace: string }, { tunnels: Tunnel[]; agents: Agent[] }];
  'tunnels/create': [TunnelRef, Tunnel];
  'tunnels/rename': [TunnelRef & { newName: string; confirm: true }, Tunnel];
  'tunnels/delete': [TunnelRef & { confirm: true }, Empty];
  'agents/start': [TunnelRef & { target?: string; projectDirectory?: string; localCa?: string }, Agent];
  'agents/restart': [TunnelRef & { target?: string; projectDirectory?: string; localCa?: string }, Agent];
  'agents/stop': [TunnelRef, Empty];
  'keys/set': [TunnelRef & { key: string }, Empty];
  'requests/subscribe': [TunnelRef & { source?: 'auto' | 'local' | 'remote' }, { subscriptionId: string; requests: RequestSummary[]; source?: 'local' | 'remote' }];
  'requests/unsubscribe': [{ subscriptionId: string }, Empty];
  'doctor/run': [TunnelRef & { target: string }, { checks: { code: string; status: 'passed' | 'warning' | 'failed'; message: string }[] }];
  'requests/detail': [RequestRef, RequestDetail];
  'requests/replay': [RequestRef & { target: string; headers?: Record<string, string>; body?: string; confirm: true }, { statusCode: number; durationMs: number }];
  'requests/exportHttp': [RequestRef & { target: string; headers?: Record<string, string>; body?: string }, { text: string }];
  'captureBodies/set': [TunnelRef & { enabled: boolean }, { enabled: boolean }];
  'keys/rotateWithoutDowntime': [TunnelRef & { confirm: true; timeoutSeconds?: number }, Empty];
  'visitorAccess/get': [TunnelRef, VisitorAccess];
  'visitorAccess/set': [TunnelRef & { default: { allow: string[] }; rules: VisitorAccess['rules']; ipRanges: string[]; confirm: true }, VisitorAccess];
  'visitorAccess/createKey': [TunnelRef & { label: string; confirm: true }, { id: string; name: string; created: string; key: string }];
  'visitorAccess/deleteKey': [TunnelRef & { id: string; confirm: true }, Empty];
  'visitorAccess/handBack': [TunnelRef & { confirm: true }, VisitorAccess];
  'visitorAccess/saveKey': [TunnelRef & { project: string; parameter: string; key: string; confirm: true }, Empty];
}
export interface Notifications {
  'auth/changed': AuthStatus;
  'tunnels/changed': { workspace: string };
  'agents/changed': { agents: Agent[] };
  'requests/event': { subscriptionId: string; request: RequestSummary };
  'connection/event': { subscriptionId: string; state: 'live' | 'reconnecting' | 'stopped'; reason?: string; source?: 'local' | 'remote' };
  /** Client lifecycle event; never sent over the protocol. */
  'backend/state': { state: 'ready' | 'reconnecting' | 'stopped'; message?: string };
}
export interface Disposable { dispose(): void }
export interface Backend extends Disposable {
  readonly capabilities: readonly string[];
  start(): Promise<void>;
  request<M extends keyof Methods>(method: M, params: Methods[M][0]): Promise<Methods[M][1]>;
  on<N extends keyof Notifications>(event: N, listener: (value: Notifications[N]) => void): Disposable;
}

export class BackendEvents {
  private readonly listeners = new Map<keyof Notifications, Set<(value: never) => void>>();
  on<N extends keyof Notifications>(event: N, listener: (value: Notifications[N]) => void): Disposable {
    let listeners = this.listeners.get(event);
    if (!listeners) this.listeners.set(event, listeners = new Set());
    const callback = listener as (value: never) => void;
    listeners.add(callback);
    return { dispose: () => { listeners.delete(callback); } };
  }
  emit<N extends keyof Notifications>(event: N, value: Notifications[N]): void {
    for (const listener of this.listeners.get(event) ?? []) listener(value as never);
  }
  dispose(): void { this.listeners.clear(); }
}
