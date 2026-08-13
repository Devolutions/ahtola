# Managed package consumer

This source-free consumer restores only packed `Devolutions.Ahtola.Data.Sqlite`
and `Devolutions.Ahtola.EntityFrameworkCore.Sqlite` artifacts. It runs on
`net8.0`, `net9.0`, and `net10.0` and verifies the public capability matrix,
managed file pooling, managed encryption, the EF Core local-only contract,
and the absence of native companion packages.

Run it through the package gate:

```powershell
./build.ps1 validate-package
```
