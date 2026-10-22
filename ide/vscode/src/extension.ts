import * as vscode from 'vscode';
import { selectCli } from './cliSelection';
import { RpcBackend, spawnSession } from './backend';
import { MockBackend } from './mockBackend';
import { Backend, Tunnel } from './protocol';
import { TunnelActions } from './actions';
import { GroundhogAuthentication } from './auth';
import { ExplorerNode, TunnelExplorer } from './explorer';
import { DebugHosting, ProjectConfig } from './debug';
import { InspectorPanels } from './inspector';
import { ActiveRequestStats } from './stats';
import { NotificationPolicy, tunnelLiterals } from './notifications';
import { tunnelConnected, tunnelLocation } from './actions';
import { VisitorAccessPanels } from './visitorAccess';
import { GroundhogDiagnostics } from './diagnostics';

async function projectConfig(folder?: vscode.WorkspaceFolder): Promise<ProjectConfig | undefined> {
  if (!folder) return;
  try {
    const value = JSON.parse(Buffer.from(await vscode.workspace.fs.readFile(vscode.Uri.joinPath(folder.uri, 'groundhog.json'))).toString('utf8')) as ProjectConfig;
    return typeof value.tunnel === 'string' ? { ...value, projectDirectory: folder.uri.fsPath } : undefined;
  } catch { return; }
}
async function targetFor(tunnel: string, folder = vscode.workspace.workspaceFolders?.[0]): Promise<string | undefined> {
  const project = await projectConfig(folder);
  if (project?.tunnel === tunnel && project.target) return project.target;
  if (!folder) return;
  const files = await vscode.workspace.findFiles(new vscode.RelativePattern(folder, '**/Properties/launchSettings.json'), '**/{bin,obj,node_modules}/**', 1);
  if (!files[0]) return;
  try {
    const settings = JSON.parse(Buffer.from(await vscode.workspace.fs.readFile(files[0])).toString('utf8')) as { profiles?: Record<string, { applicationUrl?: string }> };
    return Object.values(settings.profiles ?? {}).find(profile => profile.applicationUrl)?.applicationUrl?.split(';')[0];
  } catch { return; }
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const config = vscode.workspace.getConfiguration('groundhog');
  let cli = config.get<string>('cliPath', 'groundhog');
  context.subscriptions.push(vscode.commands.registerCommand('groundhog.installCli', () => {
    const terminal = vscode.window.createTerminal('Install Groundhog CLI');
    terminal.sendText('dotnet tool update --global Groundhog.Cli --prerelease'); terminal.show();
  }));
  const demo = context.extensionMode !== vscode.ExtensionMode.Production && config.get<boolean>('useMockBackend', false);
  let backend: Backend;
  if (demo) backend = new MockBackend();
  else {
    const selected = await selectCli(cli, vscode.Uri.joinPath(context.extensionUri, 'backend', process.platform === 'win32' ? 'groundhog.exe' : 'groundhog').fsPath, context.extension.packageJSON.groundhog.minimumCliVersion as string);
    cli = selected.path; const status = selected.status;
    if (!status.available) {
      context.subscriptions.push(vscode.window.createTreeView('groundhog.tunnels', { treeDataProvider: { getTreeItem: (item: vscode.TreeItem) => item, getChildren: () => [] } }), vscode.commands.registerCommand('groundhog.signIn', () => vscode.commands.executeCommand('groundhog.installCli')));
      void vscode.window.showWarningMessage(status.reason, 'Install Groundhog CLI').then(action => { if (action) void vscode.commands.executeCommand('groundhog.installCli'); });
      return;
    }
    backend = new RpcBackend(() => spawnSession(cli), context.extension.packageJSON.version as string);
  }
  context.subscriptions.push(backend);
  const mcpChanged = new vscode.EventEmitter<void>();
  context.subscriptions.push(mcpChanged, vscode.workspace.onDidGrantWorkspaceTrust(() => mcpChanged.fire()),
    vscode.workspace.onDidChangeConfiguration(event => { if (event.affectsConfiguration('groundhog.enableMcp')) mcpChanged.fire(); }),
    vscode.lm.registerMcpServerDefinitionProvider('groundhog.mcp', {
      onDidChangeMcpServerDefinitions: mcpChanged.event,
      provideMcpServerDefinitions: () => vscode.workspace.isTrusted && !demo && vscode.workspace.getConfiguration('groundhog').get<boolean>('enableMcp', true) ? [new vscode.McpStdioServerDefinition('Groundhog', cli, ['mcp'], {}, context.extension.packageJSON.version as string)] : [],
      resolveMcpServerDefinition: definition => vscode.workspace.isTrusted ? definition : undefined
    })
  );
  const explorer = new TunnelExplorer(backend);
  const auth = new GroundhogAuthentication(backend);
  const tree = vscode.window.createTreeView('groundhog.tunnels', { treeDataProvider: explorer });
  if (demo) tree.description = 'Demo data';
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.command = 'groundhog.requests'; status.name = 'Groundhog';
  let activeTunnel: Tunnel | undefined;
  let requestStats = { requests: 0, failures: 0, disconnected: false };
  const updateStatus = () => {
    if (!activeTunnel || explorer.auth.state !== 'signedIn') { status.hide(); return; }
    const current = explorer.tunnels.find(t => t.name === activeTunnel!.name) ?? activeTunnel;
    const connected = !requestStats.disconnected && tunnelConnected(current, explorer.agents);
    status.text = (connected ? '● ' : '○ ') + current.name + ' · ' + requestStats.requests + ' req/min · ' + requestStats.failures + ' 5xx';
    status.tooltip = current.url + '\nOpen Groundhog requests';
    status.backgroundColor = connected ? undefined : new vscode.ThemeColor('statusBarItem.errorBackground'); status.show();
  };
  const stats = new ActiveRequestStats(backend, value => { requestStats = value; if (activeTunnel) explorer.setRequestRate(activeTunnel.name, value.requests); updateStatus(); });
  const panels = new InspectorPanels(backend, context.extensionUri, (tunnel, rows) => { explorer.setRequestRate(tunnel.name, rows.filter(row => Date.parse(row.completed) >= Date.now() - 60000).length); }, async name => explorer.tunnels.find(tunnel => tunnel.name === name)?.target ?? await targetFor(name));
  const access = new VisitorAccessPanels(backend, context.extensionUri);
  const diagnostics = new GroundhogDiagnostics(backend);
  context.subscriptions.push(access, diagnostics);
  const actions = new TunnelActions(backend, {
    input: (title, value, password) => Promise.resolve(vscode.window.showInputBox({ title, value, password, ignoreFocusOut: true })),
    confirm: async message => (await vscode.window.showWarningMessage(message, { modal: true }, 'Confirm')) === 'Confirm',
    trust: async () => {
      if (vscode.workspace.isTrusted) return true;
      const action = await vscode.window.showWarningMessage('Trust this workspace before Groundhog accesses local app targets.', 'Manage workspace trust');
      if (action) await vscode.commands.executeCommand('workbench.trust.manage');
      return vscode.workspace.isTrusted;
    },
    open: async url => { await vscode.env.openExternal(vscode.Uri.parse(url)); }, copy: async value => { await vscode.env.clipboard.writeText(value); }, target: targetFor, projectDirectory: () => vscode.workspace.workspaceFolders?.[0]?.uri.fsPath
  });
  const selectTunnel = async (node?: ExplorerNode): Promise<Tunnel | undefined> => {
    if (node?.kind === 'tunnel') return node.tunnel;
    if (activeTunnel && explorer.tunnels.some(t => t.name === activeTunnel!.name)) return activeTunnel;
    const selected = await vscode.window.showQuickPick(explorer.tunnels.map(t => ({ label: t.name, description: t.url, tunnel: t })), { placeHolder: 'Choose a Groundhog tunnel' });
    return selected?.tunnel;
  };
  const command = (name: string, callback: (node?: ExplorerNode) => Promise<unknown>) => context.subscriptions.push(vscode.commands.registerCommand('groundhog.' + name, async node => {
    try { await callback(node); } catch (error) { if (!(error instanceof vscode.CancellationError)) void vscode.window.showErrorMessage((error as Error).message); }
  }));
  command('signIn', () => auth.createSession()); command('signOut', () => auth.removeSession()); command('refresh', () => explorer.refresh());
  command('switchWorkspace', async () => {
    const choice = await vscode.window.showQuickPick(explorer.workspaces.map(w => ({ label: w.name, description: w.slug, workspace: w.slug })), { placeHolder: 'Choose a Groundhog workspace' });
    if (choice) { await backend.request('workspaces/use', { workspace: choice.workspace }); await explorer.refresh(); }
  });
  command('create', async () => { if (explorer.workspace) { await actions.create(explorer.workspace); await explorer.refresh(); } });
  command('visitorAccess', async node => { const tunnel = await selectTunnel(node); if (tunnel) await access.open(tunnel); });
  command('runDiagnostics', async node => {
    const tunnel = await selectTunnel(node); const folder = node?.kind === 'tunnel' ? node.folder ?? vscode.workspace.workspaceFolders?.[0] : vscode.workspace.workspaceFolders?.[0];
    if (tunnel) await diagnostics.run(folder, tunnel, tunnel.target ?? await targetFor(tunnel.name, folder) ?? 'http://localhost:5173');
  });
  command('startApp', async node => {
    if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before starting your app.');
    const folder = node?.kind === 'tunnel' ? node.folder : undefined;
    const configurations = folder ? vscode.workspace.getConfiguration('launch', folder.uri).get<vscode.DebugConfiguration[]>('configurations', []) : [];
    if (folder && configurations.length) {
      const choice = configurations.length === 1 ? configurations[0].name : await vscode.window.showQuickPick(configurations.map(configuration => configuration.name), { placeHolder: 'Choose the app to start' });
      if (choice) await vscode.debug.startDebugging(folder, choice);
    } else await vscode.commands.executeCommand('workbench.action.debug.start');
  });
  command('configureLocalCa', async node => {
    const tunnel = await selectTunnel(node); if (!tunnel) return;
    const folder = node?.kind === 'tunnel' ? node.folder ?? vscode.workspace.workspaceFolders?.[0] : vscode.workspace.workspaceFolders?.[0];
    if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before changing local certificate settings.');
    const files = await vscode.window.showOpenDialog({ title: 'Choose a trusted local CA certificate', canSelectMany: false, filters: { Certificates: ['pem', 'crt', 'cer'] } }); if (!files?.[0]) return;
    if (explorer.agents.some(agent => agent.name === tunnel.name && agent.source === 'aspire')) throw new Error('Configure the local CA through the Aspire AppHost for this tunnel.');
    await backend.request('agents/restart', { workspace: tunnel.workspace, name: tunnel.name, localCa: files[0].fsPath, projectDirectory: folder?.uri.fsPath });
    await explorer.refresh(); await diagnostics.run(folder, tunnel, tunnel.target ?? await targetFor(tunnel.name, folder) ?? 'http://localhost:5173');
  });
  command('rotateKey', async node => {
    const tunnel = await selectTunnel(node); if (!tunnel) return;
    if (!backend.capabilities.includes('rotateWithoutDowntime')) throw new Error('Key rotation requires a newer Groundhog CLI or relay.');
    if (explorer.agents.some(agent => agent.name === tunnel.name && agent.source === 'aspire')) throw new Error('Aspire owns this tunnel. Update its key through the AppHost.');
    if (await vscode.window.showWarningMessage(`Rotate the connection key for ${tunnel.name}? Groundhog will restart its local agent before revoking the old key.`, { modal: true }, 'Rotate key') !== 'Rotate key') return;
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Rotating ${tunnel.name} connection key` }, () => backend.request('keys/rotateWithoutDowntime', { workspace: tunnel.workspace, name: tunnel.name, confirm: true, timeoutSeconds: 60 }));
    await explorer.refresh();
  });
  for (const action of ['start', 'stop', 'restart', 'open', 'copy', 'rename', 'delete', 'enterKey', 'requests'] as const) command(action, async node => {
    const tunnel = await selectTunnel(node); if (!tunnel) return;
    if (['start', 'restart', 'stop'].includes(action) && explorer.agents.some(a => a.name === tunnel.name && a.source === 'aspire')) throw new Error('This tunnel is hosted by Aspire. Manage it from the AppHost.');
    if (action === 'requests') { activeTunnel = tunnel; await stats.follow(tunnel); await panels.open(tunnel); }
    else if (action === 'restart') await actions.start(tunnel, true);
    else if (action === 'start') { await actions.start(tunnel); activeTunnel = tunnel; await stats.follow(tunnel); }
    else await actions[action](tunnel);
    await explorer.refresh(); updateStatus();
  });
  const debug = new DebugHosting(backend);
  context.subscriptions.push(debug);
  const notices = new NotificationPolicy();
  const lenses = new vscode.EventEmitter<void>();
  const notifyKey = (tunnel: Tunnel) => {
    if (notices.keyChanged(tunnel.name)) void vscode.window.showWarningMessage(`The connection key for ${tunnel.name} changed or was rejected. Enter its current key to reconnect.`);
  };
  const updateNotices = () => {
    for (const tunnel of explorer.tunnels) {
      const agent = explorer.agents.find(agent => agent.name === tunnel.name);
      if (agent?.reasonCode === 'credentialRejected') notifyKey(tunnel);
      if (notices.connection(tunnel.name, tunnelConnected(tunnel, explorer.agents) ? 'connected' : 'offline', config.get<boolean>('notifyOnDisconnect', true))) void vscode.window.showWarningMessage(`Groundhog tunnel ${tunnel.name} disconnected.`, 'Inspect requests').then(action => { if (action) void panels.open(tunnel); });
    }
    lenses.fire();
  };
  context.subscriptions.push(lenses, explorer.updated.event(updateNotices),
    backend.on('requests/event', event => { if (stats.matches(event.subscriptionId) && activeTunnel && notices.request(activeTunnel.name, event.request.statusCode, config.get<boolean>('notifyOn5xx', false))) void vscode.window.showWarningMessage(`Groundhog received a ${event.request.statusCode} response for ${activeTunnel.name}.`); }),
    backend.on('connection/event', event => { if (stats.matches(event.subscriptionId) && activeTunnel && event.reasonCode === 'credentialRejected') notifyKey(activeTunnel); }),
    vscode.languages.registerCodeLensProvider({ language: 'csharp', scheme: 'file' }, {
      onDidChangeCodeLenses: lenses.event,
      provideCodeLenses: document => tunnelLiterals(document.getText()).flatMap(({ name, offset }) => {
        const tunnel = explorer.tunnels.find(item => item.name === name); if (!tunnel) return [];
        const position = document.positionAt(offset); const range = new vscode.Range(position, position);
        const location = tunnelLocation(tunnel, explorer.agents); const node: ExplorerNode = { kind: 'tunnel', tunnel };
        return [new vscode.CodeLens(range, { title: `${location === 'offline' ? '○ offline' : '● connected'} · ${tunnel.url}`, command: 'groundhog.copy', arguments: [node] }), new vscode.CodeLens(range, { title: 'Open', command: 'groundhog.open', arguments: [node] }), new vscode.CodeLens(range, { title: 'Requests', command: 'groundhog.requests', arguments: [node] })];
      })
    })
  );
  context.subscriptions.push(explorer, auth, tree, panels, status, stats,
    vscode.authentication.registerAuthenticationProvider('groundhog', 'Groundhog', auth, { supportsMultipleAccounts: false }),
    explorer.updated.event(() => { updateStatus(); }),
    vscode.debug.registerDebugConfigurationProvider('*', {
      resolveDebugConfiguration: async (folder, configuration) => {
        const settings = vscode.workspace.getConfiguration('groundhog', folder?.uri);
        try {
          await explorer.refresh();
          const project = await projectConfig(folder);
          if (project && !project.target) project.target = await targetFor(project.tunnel, folder);
          const result = await debug.beforeLaunch(configuration, project, explorer.workspace, explorer.agents, explorer.tunnels,
            { configurations: settings.get<string[]>('debugConfigurations', []), stopAfterDebugging: settings.get<boolean>('stopHostingAfterDebugging', false) }, vscode.workspace.isTrusted);
          if (result.missingKey) {
            const node: ExplorerNode = { kind: 'tunnel', tunnel: result.missingKey, folder };
            void vscode.window.showWarningMessage('Enter a Groundhog connection key to host this tunnel. Debugging will continue without the tunnel.', 'Enter connection key').then(action => { if (action) void vscode.commands.executeCommand('groundhog.enterKey', node); });
          }
        } catch (error) { void vscode.window.showWarningMessage('Groundhog could not start hosting. Debugging will continue. ' + (error as Error).message); }
        return configuration;
      }
    }),
    vscode.debug.onDidStartDebugSession(session => debug.sessionStarted(session.id, session.configuration)),
    vscode.debug.onDidTerminateDebugSession(session => { void debug.sessionEnded(session.id, vscode.workspace.getConfiguration('groundhog', session.workspaceFolder?.uri).get<boolean>('stopHostingAfterDebugging', false), session.configuration).catch(error => vscode.window.showErrorMessage((error as Error).message)); })
  );
  try {
    await backend.start(); await explorer.refresh();
    const project = await projectConfig(vscode.workspace.workspaceFolders?.[0]); activeTunnel = explorer.tunnels.find(t => t.name === project?.tunnel); if (activeTunnel) await stats.follow(activeTunnel); updateStatus();
  } catch (error) { void vscode.window.showErrorMessage((error as Error).message); }
}
