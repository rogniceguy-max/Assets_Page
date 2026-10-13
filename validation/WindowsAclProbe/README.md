# Windows ACL service integration probe

This console tool links the real WPF project's `WindowsSmbConnectionService` and `WindowsCredentialVault` source files. It is **not** a WPF visual/UI acceptance test. Build has passed; NAS runtime checks are pending.

Build: `dotnet build validation/WindowsAclProbe/WindowsAclProbe.csproj -c Release`.

Execute the resulting `bin/Release/net8.0-windows/WindowsAclProbe.exe` with one JSON document on standard input, then close stdin. Capture stdout as the result document and check the exit code (0 only when all expectations match). Do not put credentials in command arguments or saved shell history. Feed stdin from a script that reads the existing local credential source in memory.

Input shape (placeholders, not working credentials):

```json
{
  "shareRoot": "\\\\NAS_HOST\\MuYing_ACL_Test",
  "runId": "0123456789abcdef0123456789abcdef",
  "identities": [{
    "label": "film",
    "username": "TEST_USERNAME",
    "password": "READ_FROM_LOCAL_CREDENTIAL_SOURCE",
    "domain": null,
    "spaces": [
      {"label":"film","relativePath":"FILM_TEST_DIRECTORY","expectRead":true,"expectWrite":true},
      {"label":"writing","relativePath":"WRITING_TEST_DIRECTORY","expectRead":true,"expectWrite":false},
      {"label":"public","relativePath":"PUBLIC_TEST_DIRECTORY","expectRead":true,"expectWrite":true},
      {"label":"film-personal","relativePath":"FILM_PERSONAL_TEST_DIRECTORY","expectRead":true,"expectWrite":true},
      {"label":"writing-personal","relativePath":"WRITING_PERSONAL_TEST_DIRECTORY","expectRead":false,"expectWrite":false}
    ]
  }]
}
```

Each identity requires exactly five space entries. Use distinct ASCII labels and a fresh GUID in `N` format as runId. Before running, provision small ordinary fixture files under **each** specified test space, per identity:

- `muying-acl-probe-{runId}-{label}-read.txt`
- `muying-acl-probe-{runId}-{label}-overwrite.txt`
- `muying-acl-probe-{runId}-{label}-rename.txt`
- `muying-acl-probe-{runId}-{label}-delete.txt`

Fixtures must be <= 4096 bytes, inherit the ACL under examination, and belong solely to this probe. Prepare them through the already authorized isolated test setup; do not point at real data. The tool may overwrite the first byte, rename, or delete these exact fixture names. It also attempts `CreateNew` for the corresponding `-create.txt`, never overwriting a preexisting file. Artifacts remain for evidence/explicit cleanup; there is no recursive cleanup.

Each identity runs synchronously inside its own `LogonUser(LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50)` / `WindowsIdentity.RunImpersonated` context. LogonUser success alone does not prove NAS authentication: WNet result and file operation results are recorded separately. The first real run failed at CredWrite with Win32 **1312** for all three identities before connecting to NAS. Credential Manager is unavailable in those impersonated logon contexts.

The revised tool calls the real service's `ConnectWithCredentials` method, which uses the same WNet connection path without persisting credentials. The existing application `Connect()` reads its vault and delegates to this method. A vault object with random target names remains only to satisfy the service constructor; the probe does not read, write, or delete any vault entry. It never cancels WNet connections and never falls back to the employee vault or main Windows identity. This validates only explicit-credential SMB service behavior; **credential vault operation, complete login persistence, and WPF UI acceptance remain unverified**. The prior 1312 result must be retained as a limitation, not rewritten as a successful login test.

Only the exact `MuYing_ACL_Test` share name is allowed. Relative paths cannot escape the share and server-reported reparse points are rejected. These client checks do not defeat a malicious concurrent server namespace change; use only the controlled isolated test tree. Failure matches an expected denial only for access-denied errors; missing fixtures, other network errors, and authentication errors are not counted as successful denial tests. Exception messages, credentials, file contents, and directory names are not printed. User-supplied labels identify results.
