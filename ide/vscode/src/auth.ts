import * as vscode from 'vscode';
import { Backend, AuthStatus } from './protocol';
import { hostedUrl } from './actions';

/** Accounts menu integration reflects CLI auth; it never holds an access token. */
export class GroundhogAuthentication implements vscode.AuthenticationProvider, vscode.Disposable {
  private readonly changes = new vscode.EventEmitter<vscode.AuthenticationProviderAuthenticationSessionsChangeEvent>();
  readonly onDidChangeSessions = this.changes.event;
  private status: AuthStatus = { state: 'signedOut' };
  private cancelWait?: () => void;
  private readonly subscription;
  constructor(private readonly backend: Backend) {
    this.subscription = backend.on('auth/changed', status => {
      const previous = this.sessions(); this.status = status;
      this.changes.fire({ added: this.sessions(), removed: previous, changed: [] });
    });
  }
  async getSessions(): Promise<vscode.AuthenticationSession[]> { this.status = await this.backend.request('auth/status', {}); return this.sessions(); }
  private sessions(): vscode.AuthenticationSession[] {
    return this.status.state === 'signedIn' ? [{ id: 'groundhog-cli', accessToken: '', account: { id: this.status.workspace ?? 'groundhog', label: this.status.workspace ?? 'Groundhog' }, scopes: [] }] : [];
  }
  async createSession(): Promise<vscode.AuthenticationSession> {
    this.cancelWait?.();
    const login = await this.backend.request('auth/login', {});
    const action = await vscode.window.showInformationMessage(`Sign in to Groundhog with code ${login.userCode}`, { modal: true, detail: 'Your sign-in is shared with the CLI and other Groundhog extensions.' }, 'Copy code and open browser');
    if (!action) throw new vscode.CancellationError();
    if (!hostedUrl(login.verificationUri)) throw new Error('Groundhog returned an invalid sign-in URL.');
    await vscode.env.clipboard.writeText(login.userCode);
    await vscode.env.openExternal(vscode.Uri.parse(login.verificationUri));
    const sessions = await this.getSessions();
    if (sessions.length) return sessions[0];
    return new Promise((resolve, reject) => {
      const finish = () => { clearTimeout(timer); subscription.dispose(); this.cancelWait = undefined; };
      const timer = setTimeout(() => { finish(); reject(new Error('Groundhog sign-in expired. Try signing in again.')); }, Math.min(600000, Math.max(0, Date.parse(login.expiresAt) - Date.now())));
      const subscription = this.onDidChangeSessions(event => {
        if (event.added?.[0]) { finish(); resolve(event.added[0]); }
      });
      this.cancelWait = () => { finish(); reject(new vscode.CancellationError()); };
    });
  }
  async removeSession(): Promise<void> { await this.backend.request('auth/logout', {}); }
  dispose(): void { this.cancelWait?.(); this.subscription.dispose(); this.changes.dispose(); }
}
