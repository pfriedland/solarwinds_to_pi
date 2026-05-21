# SolarWinds to PI Tag Bridge Runbook

## Prerequisites

- Windows host with network access to SolarWinds Orion and PI Data Archive.
- .NET SDK installed.
- PI AF SDK installed.
- AF SDK DLL present at:

```powershell
C:\Program Files (x86)\PIPC\AF\PublicAssemblies\4.0\OSIsoft.AFSDK.dll
```

- PI Data Archive access must be allowed by the configured PI Trust. The app does not pass PI credentials.

- The Orion account must be authorized to query `Orion.Nodes` and node custom properties.

## Build

Build the production bridge:

```powershell
dotnet build .\SolarWindsPiTagAudit.csproj -c Debug
```

Build all utilities:

```powershell
dotnet build .\QueryOrionNodes.csproj -c Debug
dotnet build .\PIArchiveConnectivity.csproj -c Debug
dotnet build .\SolarWindsPiTagAudit.csproj -c Debug
dotnet build .\SolarWindsPiBridgeService.csproj -c Debug
```

If AF SDK is installed somewhere else:

```powershell
dotnet build .\SolarWindsPiTagAudit.csproj -c Debug -p:AfSdkPath="C:\path\to\OSIsoft.AFSDK.dll"
```

## Smoke Tests

Test SolarWinds query only:

```powershell
dotnet run --project .\QueryOrionNodes.csproj -- `
  YOUR_ORION_SERVER `
  "DOMAIN\user" `
  --no-proxy `
  --skip-cert-validation
```

Test PI Data Archive connectivity only:

```powershell
dotnet run --project .\PIArchiveConnectivity.csproj -- `
  --server AndvrPIDatArchB.enelint.global
```

Test PI snapshot read:

```powershell
dotnet run --project .\PIArchiveConnectivity.csproj -- `
  --server AndvrPIDatArchB.enelint.global `
  --point "sinusoid"
```

## Dry Run

Dry run queries SolarWinds, checks PI tags, and prints what would be created and written. It does not create tags and does not write snapshots.

```powershell
dotnet run --project .\SolarWindsPiTagAudit.csproj -- `
  --orion-server YOUR_ORION_SERVER `
  --orion-username "DOMAIN\user" `
  --pi-server AndvrPIDatArchB.enelint.global `
  --orion-no-proxy `
  --orion-skip-cert-validation `
  --dry-run-create
```

Expected output includes:

- SolarWinds row count
- expected PI tag count
- PI connection identity resolved by the PI Trust
- missing tags that would be created
- snapshot values that would be written

## Live Single Run

Live mode creates missing tags and writes current snapshots.

```powershell
dotnet run --project .\SolarWindsPiTagAudit.csproj -- `
  --orion-server YOUR_ORION_SERVER `
  --orion-username "DOMAIN\user" `
  --pi-server AndvrPIDatArchB.enelint.global `
  --orion-no-proxy `
  --orion-skip-cert-validation
```

If `--orion-password` is omitted, the app prompts for it.

## Continuous Run

Run every five minutes:

```powershell
dotnet run --project .\SolarWindsPiTagAudit.csproj -- `
  --orion-server YOUR_ORION_SERVER `
  --orion-username "DOMAIN\user" `
  --pi-server AndvrPIDatArchB.enelint.global `
  --orion-no-proxy `
  --orion-skip-cert-validation `
  --forever
```

Use a custom delay:

```powershell
--forever --delay-minutes 2
```

Stop the loop with `Ctrl+C`.

## Windows Service

Build the service:

```powershell
dotnet build .\SolarWindsPiBridgeService.csproj -c Release
```

Create a config directory:

```powershell
New-Item -ItemType Directory -Force C:\apps\sw_to_pi
New-Item -ItemType Directory -Force C:\apps\sw_to_pi\logs
```

Copy and edit the example config:

```powershell
Copy-Item .\SolarWindsPiBridgeServiceConfig.example.json C:\apps\sw_to_pi\SolarWindsPiBridgeService.json
notepad C:\apps\sw_to_pi\SolarWindsPiBridgeService.json
```

Example config:

```json
{
  "orionServer": "YOUR_ORION_SERVER",
  "orionUsername": "DOMAIN\\ldap_user",
  "orionPassword": "replace-with-orion-password",
  "orionPort": 17774,
  "orionNoProxy": true,
  "orionSkipCertValidation": true,
  "piServer": "AndvrPIDatArchB.enelint.global",
  "forever": true,
  "intervalMinutes": 5,
  "dryRunCreate": false,
  "logPath": "C:\\apps\\sw_to_pi\\logs\\SolarWindsPiBridgeService.log"
}
```

Publish the service executable to the deployment directory:

```powershell
dotnet publish .\SolarWindsPiBridgeService.csproj -c Release -o C:\apps\sw_to_pi
```

Run the service executable in console mode first:

```powershell
C:\apps\sw_to_pi\SolarWindsPiBridgeService.exe --console --config C:\apps\sw_to_pi\SolarWindsPiBridgeService.json
```

Install as a Windows Service from an elevated PowerShell prompt:

```powershell
sc.exe create SolarWindsPiBridge `
  binPath= "`"C:\apps\sw_to_pi\SolarWindsPiBridgeService.exe`" --config `"C:\apps\sw_to_pi\SolarWindsPiBridgeService.json`"" `
  start= auto
```

Start and stop:

```powershell
sc.exe start SolarWindsPiBridge
sc.exe stop SolarWindsPiBridge
```

View status:

```powershell
sc.exe query SolarWindsPiBridge
```

Remove the service:

```powershell
sc.exe stop SolarWindsPiBridge
sc.exe delete SolarWindsPiBridge
```

The service does not prompt for Orion credentials. Orion LDAP credentials are supplied by the JSON config. PI credentials are not supplied; PI access is expected to resolve through the configured PI Trust.

Service loop settings:

- `forever`: `true` keeps the service polling until stopped.
- `intervalMinutes`: delay between runs. The default is `5`.
- `delayMinutes`: older alias still accepted for backward compatibility.

Protect the config file:

```powershell
icacls C:\apps\sw_to_pi\SolarWindsPiBridgeService.json /inheritance:r
icacls C:\apps\sw_to_pi\SolarWindsPiBridgeService.json /grant Administrators:F
```

Add a grant for the service identity if it does not run as LocalSystem.

## Command Options

| Option | Required | Description |
| --- | --- | --- |
| `--orion-server` | yes | SolarWinds Orion hostname or FQDN |
| `--orion-username` | yes | Orion username |
| `--orion-password` | no | Orion password; prompts if omitted |
| `--orion-port` | no | SWIS REST port, default `17774` |
| `--orion-no-proxy` | no | Disables .NET proxy use for Orion calls |
| `--orion-skip-cert-validation` | no | Ignores Orion TLS certificate validation |
| `--pi-server` | no | PI Data Archive name; default PI server is used if omitted |
| `--dry-run-create` | no | Prints planned tag creation and writes without changing PI |
| `--forever` | no | Repeats continuously |
| `--delay-minutes` | no | Loop delay; default `5` |

Service-only argument:

| Option | Required | Description |
| --- | --- | --- |
| `--config` | yes | Path to the Windows Service JSON config file |

## PI Tag Contract

Each SolarWinds node creates or updates two tags:

```text
SW <Site> <Device_Type> <NodeID> ResponseTime
SW <Site> <Device_Type> <NodeID> PercentLoss
```

Created point attributes:

| Metric | pointsource | pointtype | engunits |
| --- | --- | --- | --- |
| ResponseTime | SW | Float32 | ms |
| PercentLoss | SW | Float32 | % |

## Troubleshooting

`SWIS query failed`

- Confirm Orion hostname and port.
- Use `--orion-no-proxy` if traffic is being sent to a corporate proxy.
- Use `--orion-skip-cert-validation` only if the Orion certificate chain is not trusted.
- Validate credentials with the SolarWinds-only utility.

`SolarWinds query returned no rows`

- Confirm SolarWinds custom properties use the expected values:
  - `Site_Type = Plant`
  - `Device_Type` is one of `PI Gateways`, `IEC_104 Server`, `DNP3 Server`, `Firewall`

`No default PI Data Archive is configured`

- Pass `--pi-server <server>`.

`PI point creation failed`

- Confirm the PI Trust used by this host/process maps to a PI identity with permission to create PI points.
- Confirm the point source `SW` is permitted by local PI governance.
- Confirm tag names do not violate PI naming rules in the target archive.

`Snapshot write failed`

- Confirm the PI Trust maps to a PI identity with write access to the target PI points.
- Confirm the points were created as numeric `Float32` tags.

`Unable to load OSIsoft.AFSDK.dll`

- Confirm PI AF SDK is installed.
- Build with `-p:AfSdkPath="C:\path\to\OSIsoft.AFSDK.dll"` if it is installed outside the default path.

`NU1900` during build

- This is a NuGet vulnerability-check network warning. If the build succeeds, it does not block the executable.

Service starts then stops

- Run the service executable with `--console --config <path>` first.
- Check the configured `logPath`.
- Confirm the JSON config contains `orionServer`, `orionUsername`, and `orionPassword`.
- Confirm the service identity can read the config file and write the log file.

## Operational Notes

- Start with dry-run in any new environment.
- Run one live single pass before using `--forever`.
- Keep the console output when validating a new Orion or PI server.
- For unattended operation, run on a host/process identity covered by the configured PI Trust.
