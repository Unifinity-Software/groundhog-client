import { vi } from 'vitest';
export class EventEmitter<T> {
  private listeners = new Set<(value: T) => void>();
  event = (listener: (value: T) => void) => { this.listeners.add(listener); return { dispose: () => this.listeners.delete(listener) }; };
  fire(value: T): void { for (const listener of this.listeners) listener(value); }
  dispose(): void { this.listeners.clear(); }
}
export class CancellationError extends Error {}
export const window = { showInformationMessage: vi.fn(async () => 'Copy code and open browser'), createWebviewPanel: vi.fn(), showWarningMessage: vi.fn(), showQuickPick: vi.fn() };
export const ViewColumn = { Beside: 2 };
export const env = { clipboard: { writeText: vi.fn(async () => {}) }, openExternal: vi.fn(async () => true) };
export const Uri = { parse: (value: string) => value };
export class TreeItem { constructor(public label: string, public collapsibleState: number) {} }
export const TreeItemCollapsibleState = { None: 0, Expanded: 2 };
export const commands = { executeCommand: vi.fn(async () => {}) };
export class Range { constructor(public start: unknown, public end: unknown) {} }
export class Diagnostic { constructor(public range: Range, public message: string, public severity: number) {} }
export const DiagnosticSeverity = { Error: 0, Warning: 1 };
export class CodeAction { constructor(public title: string, public kind: string) {} }
export const CodeActionKind = { QuickFix: 'quickfix' };
export const CodeActionTriggerKind = { Invoke: 1 };
export const languages = {
  createDiagnosticCollection: vi.fn(() => ({ set: vi.fn(), dispose: vi.fn() })),
  registerCodeActionsProvider: vi.fn(() => ({ dispose: vi.fn() }))
};
Object.assign(Uri, { joinPath: (base: { toString(): string }, path: string) => ({ toString: () => base.toString() + '/' + path }) });
export const workspace = { openTextDocument: vi.fn(), findFiles: vi.fn(), isTrusted: true };
