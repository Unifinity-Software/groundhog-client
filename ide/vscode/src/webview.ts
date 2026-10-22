import * as vscode from 'vscode';
import { randomBytes } from 'node:crypto';
export function webviewHtml(webview: vscode.Webview, extensionUri: vscode.Uri, bundle: string): string {
  const nonce = randomBytes(18).toString('base64');
  const script = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'dist', bundle));
  const style = webview.asWebviewUri(vscode.Uri.joinPath(extensionUri, 'dist', 'inspector.css'));
  return `<!doctype html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';"><link rel="stylesheet" href="${style}"><title>Groundhog</title></head><body><main id="app"></main><script nonce="${nonce}" src="${script}"></script></body></html>`;
}
