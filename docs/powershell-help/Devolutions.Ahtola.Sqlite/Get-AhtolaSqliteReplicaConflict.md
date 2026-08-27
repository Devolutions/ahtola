---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/26/2026
PlatyPS schema version: 2024-05-01
title: Get-AhtolaSqliteReplicaConflict
---

# Get-AhtolaSqliteReplicaConflict

## SYNOPSIS

Inspects a managed replica's durable push conflict.

## SYNTAX

### __AllParameterSets

```
Get-AhtolaSqliteReplicaConflict -ReplicaConnection <AhtolaCloudConnection>
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Returns the immutable conflict report recorded after a replica push conflict. The operation performs no network I/O and does not mutate the local journal. It writes no object when no conflict is recorded.

## EXAMPLES

### Example 1

```powershell
$report = Get-AhtolaSqliteReplicaConflict -ReplicaConnection $replica
$report.Entries | Format-Table Sequence, Kind, Table, RowId, Eligibility
```

Inspects and displays the local changes classified by replay eligibility.

## PARAMETERS

### -ReplicaConnection

Caller-owned managed replica connection. Direct cloud connections are not supported.

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

### Ahtola.AhtolaReplicaConflictReport

The durable conflict report, or no output when no conflict is recorded.

## NOTES

Typed provider exceptions and conflict metadata are preserved.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)

