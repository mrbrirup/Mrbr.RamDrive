# Windows service and boot startup

**Status:** Pending · P3 · host application work  
**Related:** [Pending work index](../pending-work.md) · [Roadmap](../helper-functions-roadmap.md)

## Goal

Support hosting drive management in a Windows service or service-controlled process that can start configured drives at boot.

## Proposed usage

```powershell
ramdrive-service install --config .\drives.json
ramdrive-service start
```

## Requirements

- Keep service installation and SCM integration in a host project, not the core library.
- Define service account, drive visibility across user sessions, mount permissions, and WinFsp launch model.
- Store configuration with appropriate ACLs and avoid secrets in plain JSON.
- Implement orderly shutdown with the existing save-failure callback contract and a bounded service stop timeout.
- Provide logs/health status without console UI; never force-discard a failed save.
- Require explicit administrator approval for installation/removal.

## Acceptance

Service lifecycle covers install/start/stop/restart, crash recovery, configured drives, save failures, and cleanup after service termination. User-session visibility is documented.
