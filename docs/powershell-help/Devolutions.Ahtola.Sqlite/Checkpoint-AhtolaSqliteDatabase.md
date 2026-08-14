---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Checkpoint-AhtolaSqliteDatabase
---

# Checkpoint-AhtolaSqliteDatabase

## SYNOPSIS

Runs a WAL checkpoint on a local Ahtola database.

## SYNTAX

### __AllParameterSets

```
Checkpoint-AhtolaSqliteDatabase -Connection <SqliteConnection> [-Mode <string>]
 [-CommandTimeout <int>] [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Runs `PRAGMA wal_checkpoint` with the selected mode and writes the checkpoint result.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Checkpoint-AhtolaSqliteDatabase -Connection $connection -Mode Truncate -Confirm:$false
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -CommandTimeout

Command timeout in seconds. Zero is allowed; the default is 30 seconds.

```yaml
Type: System.Int32
DefaultValue: ''
SupportsWildcards: false
Aliases: []
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

### -Connection

Caller-owned Ahtola connection. It can be opened if needed but is never closed or disposed by this cmdlet.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases:
- SqliteConnection
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

### -Mode

WAL checkpoint mode: `Passive`, `Full`, `Restart`, or `Truncate`.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
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

### Ahtola.Data.Sqlite.SqliteConnection

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

### System.Object

Checkpoint status objects returned by SQLite.

## NOTES

Supports `-WhatIf` and `-Confirm`. A checkpoint can report busy when other connections prevent completion.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
