---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Optimize-AhtolaSqliteDatabase
---

# Optimize-AhtolaSqliteDatabase

## SYNOPSIS

Runs VACUUM, optionally followed by ANALYZE, on a local database.

## SYNTAX

### __AllParameterSets

```
Optimize-AhtolaSqliteDatabase -Connection <SqliteConnection> [-Analyze] [-CommandTimeout <int>]
 [-WhatIf] [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Runs `VACUUM` on a local Ahtola connection. With `Analyze`, it then runs `ANALYZE`.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    Optimize-AhtolaSqliteDatabase -Connection $connection -Analyze -Confirm:$false
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
```

## PARAMETERS

### -Analyze

Also runs `ANALYZE` after `VACUUM`.

```yaml
Type: System.Management.Automation.SwitchParameter
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

No pipeline output.

## NOTES

Supports `-WhatIf` and `-Confirm`. VACUUM can require temporary disk space and cannot run in an incompatible active transaction.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
