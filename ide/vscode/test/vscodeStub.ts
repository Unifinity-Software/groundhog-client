import { vi } from 'vitest';
export class EventEmitter<T> {
  private listeners = new Set<(value: T) => void>();
  event = (listener: (value: T) => void) => { this.listeners.add(listener); return { dispose: () => this.listeners.delete(listener) }; };
  fire(value: T): void { for (const listener of this.listeners) listener(value); }
  dispose(): void { this.listeners.clear(); }
}
export class CancellationError extends Error {}
export const window = { showInformationMessage: vi.fn(async () => 'Copy code and open browser') };
export const env = { clipboard: { writeText: vi.fn(async () => {}) }, openExternal: vi.fn(async () => true) };
export const Uri = { parse: (value: string) => value };
