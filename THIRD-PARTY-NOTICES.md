# Third-party notices

## WinFsp

Mrbr.RamDrive uses WinFsp and its .NET bindings. WinFsp is Copyright (C) Bill
Zissimopoulos and is licensed under GPLv3 with a special exception for qualifying
Free/Libre and Open Source Software (FLOSS).

The exception and its conditions are defined by the
[official WinFsp license](https://github.com/winfsp/winfsp/blob/master/License.txt).
Among the conditions, qualifying software must include the notice below and a
link to the WinFsp repository in its user interface and user-facing documentation,
and must not be linked or distributed with proprietary software when relying on
the exception.

Required attribution:

> WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos
>
> [WinFsp repository](https://github.com/winfsp/winfsp)

The console test app prints this attribution at startup. The README also includes
it. This project does not include a modified copy of the WinFsp installer; the
console app downloads the official installer when the user chooses to install the
required runtime.
