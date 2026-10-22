export interface HostAdapter { postMessage(message: unknown): void; onMessage(listener: (message: unknown) => void): void }
declare global {
  function acquireVsCodeApi(): { postMessage(message: unknown): void };
  interface Window { chrome?: { webview?: { postMessage(message: unknown): void; addEventListener(event: string, listener: (event: MessageEvent) => void): void } } }
}
export function hostAdapter(): HostAdapter {
  if (window.chrome?.webview) {
    const host = window.chrome.webview;
    return { postMessage: message => host.postMessage(message), onMessage: listener => host.addEventListener('message', event => listener(event.data)) };
  }
  const host = acquireVsCodeApi();
  return { postMessage: message => host.postMessage(message), onMessage: listener => window.addEventListener('message', event => listener(event.data)) };
}
