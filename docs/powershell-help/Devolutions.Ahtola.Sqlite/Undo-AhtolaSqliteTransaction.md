---
document type: cmdlet
external help file: Devolutions.Ahtola.PowerShell.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: Devolutions.Ahtola.Sqlite
ms.date: 08/14/2026
PlatyPS schema version: 2024-05-01
title: Undo-AhtolaSqliteTransaction
---

# Undo-AhtolaSqliteTransaction

## SYNOPSIS

Rolls back a transaction or rolls back to a savepoint.

## SYNTAX

### __AllParameterSets

```
Undo-AhtolaSqliteTransaction -Transaction <DbTransaction> [-SavepointName <string>] [-WhatIf]
 [-Confirm]
```

## ALIASES

This cmdlet has no aliases.

## DESCRIPTION

Rolls back the transaction when `SavepointName` is omitted. With a savepoint name, it rolls back only work after that savepoint.

## EXAMPLES

### Example 1

This example uses a caller-owned connection and closes it when complete.

```powershell
$connection = New-AhtolaSqliteConnection
try {
    $tx = Start-AhtolaSqliteTransaction -Connection $connection
    Undo-AhtolaSqliteTransaction -Transaction $tx -Confirm:$false
} finally { $connection | Close-AhtolaSqliteConnection -Confirm:$false }
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

### -SavepointName

Existing savepoint name to roll back to instead of completing the whole transaction.

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

### -Transaction

Caller-owned active transaction. It must belong to the supplied connection when both are present.

```yaml
Type: System.Data.Common.DbTransaction
DefaultValue: ''
SupportsWildcards: false
Aliases: []
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

### System.Data.Common.DbTransaction

A value can be piped to the pipeline-enabled parameter.

## OUTPUTS

No pipeline output.

## NOTES

Supports `-WhatIf` and `-Confirm`. This cmdlet does not dispose the transaction or its connection.

## RELATED LINKS

[PowerShell module guide](https://github.com/Devolutions/ahtola/blob/master/docs/powershell-module.md)
