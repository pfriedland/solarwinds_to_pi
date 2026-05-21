# SolarWinds to PI Tag Bridge Design

## Purpose

This project bridges selected SolarWinds Orion node metrics into OSIsoft/AVEVA PI Data Archive tags.

The production-oriented executable is `SolarWindsPiTagAudit`. It:

1. Queries SolarWinds Orion over SWIS REST.
2. Builds deterministic PI tag names from SolarWinds custom properties.
3. Connects to PI Data Archive using PI AF SDK.
4. Creates any missing PI points unless dry-run mode is enabled.
5. Writes current `ResponseTime` and `PercentLoss` values to PI snapshots.
6. Optionally repeats forever with a default five-minute delay.

## Projects

`SolarWindsPiTagAudit.csproj`

Primary bridge process. Targets `.NET Framework 4.8` because PI AF SDK is installed as a .NET Framework assembly.

`QueryOrionNodes.csproj`

SolarWinds-only diagnostic utility. Targets `net10.0` and uses only SWIS REST.

`PIArchiveConnectivity.csproj`

PI-only diagnostic utility. Targets `.NET Framework 4.8` and validates AF SDK connectivity and optional snapshot reads.

`SolarWindsPiBridgeService.csproj`

Windows Service host for the same bridge logic used by `SolarWindsPiTagAudit`. It targets `.NET Framework 4.8`, reads a JSON config file, logs to a file, and runs the bridge loop until the service is stopped.

## External Systems

SolarWinds Orion:

- Accessed through SWIS REST.
- Default endpoint: `https://<orion-server>:17774/SolarWinds/InformationService/v3/Json/Query`
- Basic authentication is used.
- `--orion-no-proxy` disables .NET proxy usage for internal Orion access.
- `--orion-skip-cert-validation` allows self-signed/private certificates.

PI Data Archive:

- Accessed through PI AF SDK.
- Default AF SDK DLL path:
  `C:\Program Files (x86)\PIPC\AF\PublicAssemblies\4.0\OSIsoft.AFSDK.dll`
- No PI username or password is supplied by the application.
- Authentication is implicit through the PI Data Archive security configuration, currently a PI Trust.

## SolarWinds Selection

The SWQL query selects `Orion.Nodes` where:

- `CustomProperties.Site_Type LIKE 'Plant'`
- `CustomProperties.Device_Type` is one of:
  - `PI Gateways`
  - `IEC_104 Server`
  - `DNP3 Server`
  - `Firewall`

Returned fields include:

- `NodeID`
- `CustomProperties.Site`
- `CustomProperties.Device_Type`
- `Caption`
- `ResponseTime`
- `PercentLoss`
- operational metadata such as status, vendor, IP address, and machine type

## Tag Naming

Each SolarWinds node produces two PI tags:

```text
SW <Site> <Device_Type> <NodeID> ResponseTime
SW <Site> <Device_Type> <NodeID> PercentLoss
```

Examples:

```text
SW AZURSO PI Gateways 123 ResponseTime
SW AZURSO PI Gateways 123 PercentLoss
```

The tag-name contract depends on `Site`, `Device_Type`, and `NodeID`. If `Site` or `Device_Type` changes in SolarWinds, the bridge will expect a different PI tag name on the next run.

## PI Point Attributes

Missing tags are created with minimal point attributes:

| Metric | pointtype | pointsource | engunits |
| --- | --- | --- | --- |
| ResponseTime | Float32 | SW | ms |
| PercentLoss | Float32 | SW | % |

The descriptor is:

```text
SolarWinds <metric> for <SolarWinds Caption>
```

## Runtime Flow

Single run:

1. Parse command-line options.
2. Prompt for the Orion password if `--orion-password` is omitted.
3. Query SolarWinds through SWIS REST.
4. Fail if SolarWinds returns zero rows.
5. Build expected PI tags.
6. Connect to PI and identify missing tags.
7. In dry-run mode:
   - Print missing tags that would be created.
   - Print snapshot values that would be written.
   - Do not change PI.
8. In live mode:
   - Create missing tags.
   - Write snapshot values for all expected tags.
9. Skip snapshot writes where SolarWinds returned a null or nonnumeric value.

Forever mode:

- Enabled with `--forever`.
- Default loop delay is five minutes.
- Override with `--delay-minutes <number>`.
- Per-run failures are logged and the process continues after the delay.

Windows Service mode:

- Service executable: `SolarWindsPiBridgeService.exe`
- Required service argument: `--config <json-config-path>`
- The JSON config supplies Orion host, Orion LDAP credentials, PI server, delay, proxy/certificate options, and log path.
- The service does not prompt for credentials.
- Console output from the shared bridge runner is redirected to the configured log file.

## Failure Behavior

The process exits with code `1` for unrecoverable single-run failures such as:

- invalid command-line options
- SWIS HTTP errors
- malformed SWIS response
- missing required SolarWinds custom properties
- PI connection or authorization failures
- PI point creation or snapshot write failures

In `--forever` mode, per-run failures are logged and the process continues.

## Security Notes

- Prefer omitting `--orion-password` so the app prompts interactively.
- If running unattended, provide credentials through a secure scheduler or service account mechanism.
- For the Windows Service, Orion credentials are read from the JSON config file because services cannot safely prompt.
- Lock down the config file ACL so only administrators and the service identity can read it.
- PI credentials are not stored in the config file. PI access depends on the configured PI Trust matching the service host/process characteristics.
- `--orion-skip-cert-validation` should only be used when certificate trust cannot be configured properly.

## Known Limitations

- The bridge writes current snapshots only; it does not backfill historical values.
- It does not delete or rename PI tags if SolarWinds custom properties change.
- It checks and writes tags sequentially.
- It does not currently persist structured logs to a file or Windows Event Log.
