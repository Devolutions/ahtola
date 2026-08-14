---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Backup-AhtolaSqliteDatabase
---

# Backup-AhtolaSqliteDatabase

## SYNOPSIS

Copies a local Ahtola SQLite database to another connection.

## SYNTAX

### __AllParameterSets

```
Backup-AhtolaSqliteDatabase -SourceConnection <SqliteConnection>
 -DestinationConnection <SqliteConnection> [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Performs a page-level backup from a local source connection to a distinct local destination connection. Both connections remain caller-owned.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$source = New-AhtolaSqliteConnection
$destination = New-AhtolaSqliteConnection
try { Backup-AhtolaSqliteDatabase -SourceConnection $source -DestinationConnection $destination -Confirm:$false }
finally { $source | Close-AhtolaSqliteConnection -Confirm:$false; $destination | Close-AhtolaSqliteConnection -Confirm:$false }
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

### -DestinationConnection

Local connection receiving the backup. It must differ from `SourceConnection`.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -SourceConnection

Local connection to back up. It must differ from `DestinationConnection`.

```yaml
Type: Ahtola.Data.Sqlite.SqliteConnection
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: true
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

None. This cmdlet does not accept pipeline input.

## OUTPUTS

No pipeline output.

## NOTES

Supports `-WhatIf` and `-Confirm`. Source and destination must be different connection instances.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
