# Interactive CLI implementation plan

## Goal

Provide keyboard navigation and useful tunnel diagnostics alongside scriptable commands.
Keep `host` compatible and keep credentials out of saved profiles, output and screenshots.

## Delivery

1. Add Spectre.Console rendering and an interactive home screen. Redirected input/output uses help and plain output.
2. Add saved local profiles, guided setup and selection. Profiles contain targets and relay overrides, never keys.
3. Add bounded request history and session statistics, with filtering, structured output and live hosting views.
4. Add local agent discovery/control so another invocation can inspect status, traffic and diagnostics.
5. Integrate account authentication and remote tunnel list/describe/management against the actual service contract.
   The public v1 hosting protocol does not define these APIs; do not invent compatible endpoints.
6. Document commands, data scope, keyboard controls and troubleshooting with screenshots from the actual renderer.
7. Build Release, run all client suites, formatting checks, package consumers and packed CLI smoke tests locally.
8. Merge a passing PR, publish the next shared preview version, and verify published package consumers.

## Design rules

- `list` means remote tunnel inventory; `ps` means locally running agents.
- `describe` explains a tunnel; `config get` retrieves a setting.
- `host` runs in the foreground. Background lifecycle commands require explicit process supervision.
- Menus and live displays run sequentially, with one renderer owning terminal output.
- Escape and quit actions remain visible; every operation also has a command interface.
- Label session statistics and history retention; WebSocket completion durations are not HTTP latency samples.
- Bound memory and local control responses; exclude headers, bodies, query strings and credentials.
- Use fake time for timing tests and the existing shared fake relay for forwarding tests.

## Service dependency

The sibling relay repository supplied the existing workspace API contract. A coordinated relay change adds
browser-approved CLI credentials. Client and relay changes are validated separately and published together;
deploying the upgraded relay is required for remote login. Existing key-based hosting remains compatible.

## Implemented first preview

Home menus, searchable remote tunnel selection, guided profiles, live hosting views, local process status/control,
background start/restart, readiness waiting, connection logs, local request filtering and session statistics,
remote inventory/details/create/rename/delete/key management, remote usage and metadata queries, account/workspace
selection, diagnostics and JSON read output. Illustrated docs use the tested renderer with synthetic data.

Shell completion, multiple saved account/relay contexts, automatic startup services and persistent local traffic
archives remain future enhancements. `update` is represented by the explicit `rename` command because that is the
currently supported general tunnel setting mutation.
