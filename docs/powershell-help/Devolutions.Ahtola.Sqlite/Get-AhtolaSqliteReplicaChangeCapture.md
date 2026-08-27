---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/26/2026
PlatyPS schema version: 2024-05-01
title: Get-AhtolaSqliteReplicaChangeCapture
---

# Get-AhtolaSqliteReplicaChangeCapture

## SYNOPSIS

Projects pending managed-replica changes into the public CDC contract.

## SYNTAX

### __AllParameterSets

```
Get-AhtolaSqliteReplicaChangeCapture -ReplicaConnection <AhtolaCloudConnection>
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Returns a read-only snapshot of pending, not-yet-pushed local row changes. The projection performs no network I/O, does not create a CDC table, and does not advance the push acknowledgement watermark.

## EXAMPLES

### Example 1

```powershell
$capture = Get-AhtolaSqliteReplicaChangeCapture -ReplicaConnection $replica
$capture.Rows | Select-Object ChangeId, ChangeType, TableName, RowId
```

Displays the pending projected CDC rows.

## PARAMETERS

### -ReplicaConnection

Caller-owned managed replica connection whose pending local changes are projected.

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

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### Ahtola.PSSqlite.AhtolaCloudConnection

A managed replica connection can be piped to this cmdlet.

## OUTPUTS

### Ahtola.AhtolaReplicaChangeCaptureBatch

The batch metadata and projected pending rows.

## NOTES

Projection fails closed with `AhtolaReplicaChangeCaptureException` for an active transaction or a change that cannot be represented safely.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)

