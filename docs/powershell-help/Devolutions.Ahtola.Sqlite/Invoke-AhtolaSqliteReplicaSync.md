---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Invoke-AhtolaSqliteReplicaSync
---

# Invoke-AhtolaSqliteReplicaSync

## SYNOPSIS

Synchronizes a managed Turso Cloud replica with its remote endpoint.

## SYNTAX

### __AllParameterSets

```
Invoke-AhtolaSqliteReplicaSync -ReplicaConnection <AhtolaCloudConnection> [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Pushes local replica changes and pulls remote changes for a connection created with `-ReplicaPath`. It does not apply to a direct cloud connection.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$token = Read-Host -AsSecureString -Prompt 'Turso auth token'
$replica = New-AhtolaSqliteConnection -TursoUrl 'libsql://example.turso.io' -AuthToken $token -ReplicaPath ./replica.db
try { Invoke-AhtolaSqliteReplicaSync -ReplicaConnection $replica -Confirm:$false }
finally { $replica | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -Confirm

Prompts you for confirmation before running the cmdlet.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- cf
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ReplicaConnection

Caller-owned managed Turso replica created with `New-AhtolaSqliteConnection -ReplicaPath`.

```yaml
Type: Ahtola.PSSqlite.AhtolaCloudConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- Connection
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: true
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -WhatIf

Runs the command in a mode that only reports what would happen without performing the actions.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- wi
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### Ahtola.PSSqlite.AhtolaCloudConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### Ahtola.AhtolaSyncResult

An `AhtolaSyncResult` with outcome and statistics.

## NOTES

Supports `-WhatIf` and `-Confirm`. Sync failures, including conflicts, are surfaced to the caller.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
