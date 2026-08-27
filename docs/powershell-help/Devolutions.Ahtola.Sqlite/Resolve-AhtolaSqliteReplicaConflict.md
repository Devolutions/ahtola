---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/26/2026
PlatyPS schema version: 2024-05-01
title: Resolve-AhtolaSqliteReplicaConflict
---

# Resolve-AhtolaSqliteReplicaConflict

## SYNOPSIS

Rebases eligible changes or discards unresolved managed-replica changes.

## SYNTAX

### RebaseEligible (Default)

```
Resolve-AhtolaSqliteReplicaConflict -ReplicaConnection <AhtolaCloudConnection> [-RebaseEligible]
 [-WhatIf] [-Confirm]
```

### DiscardUnresolved

```
Resolve-AhtolaSqliteReplicaConflict -ReplicaConnection <AhtolaCloudConnection>
 -DiscardUnresolvedChanges -AcknowledgeDataLoss [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Applies an explicit resolution to a durable managed-replica push conflict. Rebase pulls a fresh remote base and replays only changes the provider classified as eligible. Discard permanently removes unresolved local changes and requires explicit data-loss acknowledgement.

## EXAMPLES

### Example 1

```powershell
Resolve-AhtolaSqliteReplicaConflict -ReplicaConnection $replica -RebaseEligible -Confirm:$false
```

Pulls and replays eligible changes. `-ReplayEligible` is an alias for `-RebaseEligible`.

### Example 2

```powershell
Resolve-AhtolaSqliteReplicaConflict -ReplicaConnection $replica `
    -DiscardUnresolvedChanges -AcknowledgeDataLoss -Confirm
```

Permanently discards unresolved local changes after confirmation.

## PARAMETERS

### -AcknowledgeDataLoss

Explicitly acknowledges that unresolved locally committed changes will never be pushed.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: DiscardUnresolved
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Confirm

Prompts you for confirmation before applying the resolution.

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

### -DiscardUnresolvedChanges

Permanently removes the still-unresolved entries from the local change journal.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: DiscardUnresolved
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -RebaseEligible

Pulls a fresh remote base and replays only changes classified as eligible.

```yaml
Type: System.Management.Automation.SwitchParameter
DefaultValue: ''
SupportsWildcards: false
Aliases:
- ReplayEligible
ParameterSets:
- Name: RebaseEligible
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

Caller-owned managed replica connection with a durable conflict marker.

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

Reports the resolution without pulling, replaying, or discarding changes.

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

A managed replica connection can be piped to this cmdlet.

## OUTPUTS

### Ahtola.AhtolaReplicaConflictResolutionResult

The applied resolution, counts, remaining conflict, and optional sync result.

## NOTES

Both resolution modes support `-WhatIf`. Typed Ahtola exceptions and metadata are preserved.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)

